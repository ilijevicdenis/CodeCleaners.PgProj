// EP-VS — the editor factory that gives .sql files INSIDE PgProj projects a PostgreSQL editor.
// Registered for the .sql extension at a higher priority than the built-in T-SQL editor, it claims
// a file only when the owning project is the PgProj project type and DECLINES everything else
// (VS_E_UNSUPPORTEDFORMAT → the shell falls through to the next factory), so ordinary SQL Server
// projects and loose .sql files keep their normal T-SQL experience. The editor it creates is the
// standard VS text editor over a buffer whose content type is "pgsql" — which is what detaches the
// T-SQL IntelliSense (the source of the SQL80001 noise on PostgreSQL syntax) and attaches the
// PgProj language client instead.
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace PgProj.VisualStudio.ProjectSystem.Editors
{
    [Guid(GuidString)]
    internal sealed class PgSqlEditorFactory : IVsEditorFactory
    {
        /// <summary>Mirrored in the pkgdef Editors registration (via ProvideEditorFactory).</summary>
        public const string GuidString = "b0000000-0000-0000-0000-0000000000a5";

        private ServiceProvider _vsServiceProvider;
        private IOleServiceProvider _oleServiceProvider;

        public int SetSite(IOleServiceProvider psp)
        {
            _oleServiceProvider = psp;
            _vsServiceProvider = new ServiceProvider(psp);
            return VSConstants.S_OK;
        }

        public int Close() => VSConstants.S_OK;

        /// <summary>
        /// Writes the claim/decline decision (with both identity probes' raw results) to the VS
        /// ActivityLog, so a `devenv /log` run shows exactly why a .sql file did or didn't get the
        /// PostgreSQL editor — this chain failed invisibly once already.
        /// </summary>
        private void LogDecision(string document, int typeGuidHr, Guid projectType, int capsHr, string capabilities, bool claimed, IVsHierarchy pvHierForName)
        {
            try
            {
                if (_vsServiceProvider.GetService(typeof(SVsActivityLog)) is not IVsActivityLog log) return;
                string hierName = "<unknown>";
                try
                {
                    if (pvHierForName is not null
                        && ErrorHandler.Succeeded(pvHierForName.GetProperty(VSConstants.VSITEMID_ROOT,
                               (int)__VSHPROPID.VSHPROPID_Name, out var nameObj)))
                        hierName = nameObj as string ?? "<null>";
                }
                catch { }
                log.LogEntry((uint)__ACTIVITYLOG_ENTRYTYPE.ALE_INFORMATION, "PgSqlEditorFactory",
                    $"{(claimed ? "CLAIMED" : "DECLINED")} '{document}' — hierarchy='{hierName}'; TypeGuid hr=0x{typeGuidHr:X8} value={projectType:B}; " +
                    $"Capabilities hr=0x{capsHr:X8} value='{capabilities}'");
            }
            catch
            {
                // logging must never affect the claim decision
            }
        }

        public int MapLogicalView(ref Guid rguidLogicalView, out string pbstrPhysicalView)
        {
            pbstrPhysicalView = null; // the text view is the only physical view
            return rguidLogicalView == VSConstants.LOGVIEWID_Primary
                || rguidLogicalView == VSConstants.LOGVIEWID_Code
                || rguidLogicalView == VSConstants.LOGVIEWID_TextView
                || rguidLogicalView == VSConstants.LOGVIEWID_Debugging
                ? VSConstants.S_OK
                : VSConstants.E_NOTIMPL;
        }

        public int CreateEditorInstance(uint grfCreateDoc, string pszMkDocument, string pszPhysicalView,
            IVsHierarchy pvHier, uint itemid, IntPtr punkDocDataExisting,
            out IntPtr ppunkDocView, out IntPtr ppunkDocData, out string pbstrEditorCaption,
            out Guid pguidCmdUI, out int pgrfCDW)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pbstrEditorCaption = string.Empty;
            pguidCmdUI = Guid.Empty;
            pgrfCDW = 0;

            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0)
                return VSConstants.E_INVALIDARG;

            // Only files that live in a PgProj project. Anything else (SSDT projects, misc files)
            // is declined so the next-priority editor factory takes over. Two project-identity
            // probes, either suffices: the classic TypeGuid hierarchy property, and the CPS-native
            // ProjectCapabilities string ("PgProj", declared by the SDK and the type registration)
            // — a CPS hierarchy does not necessarily answer VSHPROPID_TypeGuid with the type guid.
            if (pvHier is null)
                return VSConstants.VS_E_UNSUPPORTEDFORMAT;

            var typeGuidHr = pvHier.GetGuidProperty(VSConstants.VSITEMID_ROOT,
                (int)__VSHPROPID.VSHPROPID_TypeGuid, out var projectType);
            var typeMatch = ErrorHandler.Succeeded(typeGuidHr)
                && projectType == new Guid(PgProjGuids.ProjectTypeGuidString);

            var capsHr = pvHier.GetProperty(VSConstants.VSITEMID_ROOT,
                (int)__VSHPROPID5.VSHPROPID_ProjectCapabilities, out var capsObj);
            var capabilities = capsObj as string ?? string.Empty;
            var capabilityMatch = ErrorHandler.Succeeded(capsHr)
                && (" " + capabilities + " ").IndexOf(" PgProj ", StringComparison.OrdinalIgnoreCase) >= 0;

            LogDecision(pszMkDocument, typeGuidHr, projectType, capsHr, capabilities, typeMatch || capabilityMatch, pvHier);
            if (!typeMatch && !capabilityMatch)
                return VSConstants.VS_E_UNSUPPORTEDFORMAT;

            var localRegistry = (ILocalRegistry)_vsServiceProvider.GetService(typeof(SLocalRegistry));
            if (localRegistry is null)
                return VSConstants.E_FAIL;

            IVsTextLines textLines;
            if (punkDocDataExisting == IntPtr.Zero)
            {
                var iidTextLines = typeof(IVsTextLines).GUID;
                ErrorHandler.ThrowOnFailure(localRegistry.CreateInstance(typeof(VsTextBufferClass).GUID, null,
                    ref iidTextLines, (uint)CLSCTX.CLSCTX_INPROC_SERVER, out var bufferPtr));
                try { textLines = (IVsTextLines)Marshal.GetObjectForIUnknown(bufferPtr); }
                finally { Marshal.Release(bufferPtr); }
                ((IObjectWithSite)textLines).SetSite(_oleServiceProvider);

                // The whole point: the buffer speaks PostgreSQL, not T-SQL. Two settings, both
                // required: the content type itself, AND turning OFF language detection — when the
                // docdata loads the file (after this method returns), the buffer re-detects a
                // language from the .sql extension and OVERWRITES the content type we just set;
                // VsBufferDetectLangSID=false keeps the explicit assignment authoritative.
                var detectLangKey = VSConstants.VsTextBufferUserDataGuid.VsBufferDetectLangSID_guid;
                ((IVsUserData)textLines).SetData(ref detectLangKey, false);
                var contentTypeKey = VSConstants.VsTextBufferUserDataGuid.VsBufferContentType_guid;
                ((IVsUserData)textLines).SetData(ref contentTypeKey, PgSqlContentType.Name);
            }
            else
            {
                // The document is already open with some doc data (e.g. reopened from another view);
                // we can only reuse it when it is a text buffer.
                textLines = Marshal.GetObjectForIUnknown(punkDocDataExisting) as IVsTextLines;
                if (textLines is null)
                    return VSConstants.VS_E_INCOMPATIBLEDOCDATA;
            }

            var iidCodeWindow = typeof(IVsCodeWindow).GUID;
            ErrorHandler.ThrowOnFailure(localRegistry.CreateInstance(typeof(VsCodeWindowClass).GUID, null,
                ref iidCodeWindow, (uint)CLSCTX.CLSCTX_INPROC_SERVER, out var windowPtr));
            IVsCodeWindow window;
            try { window = (IVsCodeWindow)Marshal.GetObjectForIUnknown(windowPtr); }
            finally { Marshal.Release(windowPtr); }
            ErrorHandler.ThrowOnFailure(window.SetBuffer(textLines));

            ppunkDocView = Marshal.GetIUnknownForObject(window);
            ppunkDocData = Marshal.GetIUnknownForObject(textLines);
            pbstrEditorCaption = string.Empty;
            // Standard text-editor command UI → all text-editor keybindings/menus just work.
            pguidCmdUI = VSConstants.GUID_TextEditorFactory;
            return VSConstants.S_OK;
        }
    }
}
