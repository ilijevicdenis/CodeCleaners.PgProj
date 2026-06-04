// Object templates for "Add Object". The engine has no template files yet (EP-TEMPLATES #—), so the
// extension ships its own minimal DDL snippets. Pure (no vscode import) for unit-testing the rendered
// text + suggested folder/filename. ${defaultSchema} and ${name} are substituted before insertion.

export type ObjectTemplateKind = "table" | "view" | "function";

export interface ObjectTemplate {
  kind: ObjectTemplateKind;
  /** On-disk folder (matches the sample project's layout). */
  folder: string;
  /** Quick-pick label. */
  label: string;
}

export const OBJECT_TEMPLATES: ObjectTemplate[] = [
  { kind: "table", folder: "Tables", label: "Table" },
  { kind: "view", folder: "Views", label: "View" },
  { kind: "function", folder: "Functions", label: "Function" },
];

/** Render the DDL body for a new object given the chosen schema + object name. */
export function renderTemplate(
  kind: ObjectTemplateKind,
  schema: string,
  name: string
): string {
  const qualified = `${schema}.${name}`;
  switch (kind) {
    case "table":
      return `CREATE TABLE ${qualified} (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY
);
`;
    case "view":
      return `CREATE VIEW ${qualified} AS
SELECT 1 AS placeholder;
`;
    case "function":
      return `CREATE FUNCTION ${qualified}()
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    -- TODO: implement
END;
$$;
`;
  }
}

/** The suggested project-relative path for a new object file (e.g. "Tables/afd.customers.sql"). */
export function templateRelativePath(
  kind: ObjectTemplateKind,
  schema: string,
  name: string
): string {
  const folder = OBJECT_TEMPLATES.find((t) => t.kind === kind)!.folder;
  return `${folder}/${schema}.${name}.sql`;
}
