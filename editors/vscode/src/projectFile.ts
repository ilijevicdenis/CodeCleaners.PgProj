// Pure .pgproj XML editing helpers (no vscode import — unit-testable). The engine reads
// <TargetPostgresVersion> from the first <PropertyGroup> (DatabaseProject.Load), so we mutate that
// element in place if present, or inject it into the first PropertyGroup otherwise.

/** Set (or insert) <TargetPostgresVersion> in a .pgproj XML string, returning the new text. */
export function setTargetVersionInProjectXml(xml: string, version: string): string {
  const existing = /<TargetPostgresVersion>\s*[^<]*\s*<\/TargetPostgresVersion>/i;
  if (existing.test(xml)) {
    return xml.replace(existing, `<TargetPostgresVersion>${version}</TargetPostgresVersion>`);
  }
  // Inject before the first </PropertyGroup>, preserving the indentation of that group's children.
  const closing = xml.match(/([ \t]*)<\/PropertyGroup>/i);
  if (closing) {
    const indent = closing[1] ?? "  ";
    const childIndent = indent.length > 0 ? indent + "  " : "    ";
    return xml.replace(
      closing[0],
      `${childIndent}<TargetPostgresVersion>${version}</TargetPostgresVersion>\n${closing[0]}`
    );
  }
  return xml; // No PropertyGroup found; leave untouched rather than corrupt the file.
}

/** Read <DefaultSchema> (default "public") from a .pgproj XML string. */
export function readDefaultSchemaFromXml(xml: string): string {
  const m = xml.match(/<DefaultSchema>\s*([^<\s]+)\s*<\/DefaultSchema>/i);
  return m?.[1] ?? "public";
}
