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
            // is declined so the next-priority editor factory takes over.
            if (pvHier is null
                || ErrorHandler.Failed(pvHier.GetGuidProperty(VSConstants.VSITEMID_ROOT,
                       (int)__VSHPROPID.VSHPROPID_TypeGuid, out var projectType))
                || projectType != new Guid(PgProjGuids.ProjectTypeGuidString))
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

                // The whole point: the buffer speaks PostgreSQL, not T-SQL.
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
