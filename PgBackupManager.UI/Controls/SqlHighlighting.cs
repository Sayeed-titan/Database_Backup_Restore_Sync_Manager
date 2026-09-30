using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using PgBackupManager.UI.Services;

namespace PgBackupManager.UI.Controls;

// One SQL grammar covering the common keywords of PostgreSQL, SQL Server,
// Oracle, MySQL and SQLite. Colours are re-applied on every theme switch.
public static class SqlHighlighting
{
    public static readonly string[] Keywords =
    {
        "ADD","ALL","ALTER","AND","ANY","AS","ASC","BEGIN","BETWEEN","BY","CASCADE","CASE","CAST","CHECK","COLUMN","COMMIT","CONSTRAINT","CREATE",
        "CROSS","CURRENT_DATE","CURRENT_TIMESTAMP","CURSOR","DATABASE","DECLARE","DEFAULT","DELETE","DESC","DISTINCT","DO","DROP","ELSE","ELSIF","END",
        "EXCEPT","EXCEPTION","EXEC","EXECUTE","EXISTS","EXPLAIN","FETCH","FOR","FOREIGN","FROM","FULL","FUNCTION","GRANT","GROUP","HAVING","IF","IN",
        "INDEX","INNER","INSERT","INTERSECT","INTO","IS","JOIN","KEY","LANGUAGE","LEFT","LIKE","ILIKE","LIMIT","LOOP","MERGE","MINUS","NOT","NULL","OFFSET",
        "ON","OR","ORDER","OUTER","OVER","PACKAGE","PARTITION","PERFORM","PRIMARY","PROCEDURE","RAISE","RECURSIVE","REFERENCES","REPLACE","RETURN",
        "RETURNING","RETURNS","REVOKE","RIGHT","ROLLBACK","ROWS","SCHEMA","SELECT","SEQUENCE","SET","TABLE","THEN","TO","TOP","TRIGGER","TRUNCATE",
        "UNION","UNIQUE","UPDATE","USING","VALUES","VIEW","WHEN","WHERE","WHILE","WITH","GO","TRUE","FALSE","BODY","TYPE","VACUUM","ANALYZE","COPY",
        "WINDOW","LATERAL","NATURAL","CONFLICT","NOTHING","IDENTITY","GENERATED","ALWAYS","EXTENSION","OWNER","NOCOUNT","PRINT","DELIMITER","PRAGMA",
    };

    public static readonly string[] Types =
    {
        "INT","INTEGER","BIGINT","SMALLINT","TINYINT","NUMERIC","DECIMAL","NUMBER","REAL","FLOAT","DOUBLE","PRECISION","BOOLEAN","BOOL","BIT","CHAR",
        "VARCHAR","VARCHAR2","NVARCHAR","NVARCHAR2","NCHAR","TEXT","CLOB","NCLOB","BLOB","BYTEA","RAW","DATE","TIME","TIMESTAMP","TIMESTAMPTZ","DATETIME",
        "DATETIME2","INTERVAL","UUID","UNIQUEIDENTIFIER","JSON","JSONB","XML","SERIAL","BIGSERIAL","MONEY","VARBINARY","IMAGE","LONGTEXT","SYS_REFCURSOR",
    };

    private static IHighlightingDefinition? _def;

    public static IHighlightingDefinition Definition => _def ??= Build();

    private static IHighlightingDefinition Build()
    {
        var xshd = $@"<?xml version=""1.0""?>
<SyntaxDefinition name=""SQL-Universal"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#6A9955"" />
  <Color name=""String"" foreground=""#B45309"" />
  <Color name=""Number"" foreground=""#7C3AED"" />
  <Color name=""Keyword"" foreground=""#0A7D9A"" fontWeight=""bold"" />
  <Color name=""Type"" foreground=""#DB2777"" />
  <Color name=""Variable"" foreground=""#0891B2"" />
  <Color name=""Quoted"" foreground=""#334155"" />
  <RuleSet ignoreCase=""true"">
    <Span color=""Comment"" begin=""--"" />
    <Span color=""Comment"" multiline=""true"" begin=""/\*"" end=""\*/"" />
    <Span color=""String"" multiline=""true"" begin=""'"" end=""'"" />
    <Span color=""String"" multiline=""true"" begin=""\$\$"" end=""\$\$"" />
    <Span color=""Quoted"" begin=""&quot;"" end=""&quot;"" />
    <Span color=""Quoted"" begin=""\["" end=""\]"" />
    <Span color=""Quoted"" begin=""`"" end=""`"" />
    <Keywords color=""Keyword"">{string.Join("", Keywords.Select(k => $"<Word>{k}</Word>"))}</Keywords>
    <Keywords color=""Type"">{string.Join("", Types.Select(k => $"<Word>{k}</Word>"))}</Keywords>
    <Rule color=""Variable"">[@:][A-Za-z_][A-Za-z0-9_]*</Rule>
    <Rule color=""Number"">\b\d+(\.\d+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
        using var reader = XmlReader.Create(new StringReader(xshd));
        var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ApplyTheme(def);
        ThemeService.ThemeChanged += (_, _) => ApplyTheme(def);
        return def;
    }

    private static void ApplyTheme(IHighlightingDefinition def)
    {
        bool dark = ThemeService.IsDark;
        var accent = (System.Windows.Application.Current.TryFindResource("Teal") as SolidColorBrush)?.Color ?? Colors.Teal;
        Set(def, "Comment", dark ? "#6A9955" : "#15803D");
        Set(def, "String", dark ? "#CE9178" : "#B45309");
        Set(def, "Number", dark ? "#B5CEA8" : "#7C3AED");
        Set(def, "Type", dark ? "#4EC9B0" : "#BE185D");
        Set(def, "Variable", dark ? "#9CDCFE" : "#0E7490");
        Set(def, "Quoted", dark ? "#DCDCAA" : "#334155");
        def.GetNamedColor("Keyword").Foreground = new SimpleHighlightingBrush(accent);
    }

    private static void Set(IHighlightingDefinition def, string name, string hex) =>
        def.GetNamedColor(name).Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
}
