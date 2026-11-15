using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace FleetManager.Services;

public static class MaterialIqExcel
{
    public record Sheet(string Title, string[] Headers, JsonElement[][] Rows);

    public static byte[] Create(string user, DateTimeOffset now, IReadOnlyList<Sheet> sheets, byte[] logo)
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace p = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace t = "http://schemas.openxmlformats.org/package/2006/content-types";
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var workbookSheets = new XElement(s + "sheets");
            var relationships = new XElement(p + "Relationships");
            XElement? contentTypes = null;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i]; var n = i + 1;
                using var source = new ZipArchive(new MemoryStream(Create(sheet.Title, user, now, sheet.Headers, sheet.Rows, logo)));
                using (var reader = new StreamReader(source.GetEntry("[Content_Types].xml")!.Open()))
                {
                    var types = XElement.Parse(reader.ReadToEnd());
                    if (contentTypes == null) {
                        contentTypes = new XElement(types);
                        contentTypes.Elements(t + "Override").Where(x => ((string?)x.Attribute("PartName"))?.StartsWith("/xl/worksheets/") == true || ((string?)x.Attribute("PartName"))?.StartsWith("/xl/drawings/") == true).Remove();
                    }
                    foreach (var part in types.Elements(t + "Override").Where(x => ((string?)x.Attribute("PartName"))?.StartsWith("/xl/worksheets/") == true || ((string?)x.Attribute("PartName"))?.StartsWith("/xl/drawings/") == true).ToArray())
                        contentTypes.Add(new XElement(part.Name, new XAttribute("PartName", part.Attribute("PartName")!.Value.Replace("sheet1.xml", $"sheet{n}.xml").Replace("drawing1.xml", $"drawing{n}.xml")), new XAttribute("ContentType", part.Attribute("ContentType")!.Value)));
                }
                foreach (var entry in source.Entries)
                {
                    if (entry.FullName is "[Content_Types].xml" or "xl/workbook.xml" or "xl/_rels/workbook.xml.rels") continue;
                    var specific = entry.FullName.StartsWith("xl/worksheets/") || entry.FullName.StartsWith("xl/drawings/");
                    if (i > 0 && !specific) continue;
                    var path = entry.FullName.Replace("sheet1.xml", $"sheet{n}.xml").Replace("drawing1.xml", $"drawing{n}.xml");
                    using var dest = target.CreateEntry(path).Open();
                    using var src = entry.Open();
                    if (specific && path.EndsWith(".rels")) {
                        using var reader = new StreamReader(src); using var writer = new StreamWriter(dest, new UTF8Encoding(false));
                        writer.Write(reader.ReadToEnd().Replace("drawing1.xml", $"drawing{n}.xml"));
                    } else src.CopyTo(dest);
                }
                var baseName = new string(sheet.Title.Where(c => !"[]:*?/\\".Contains(c) && !char.IsControl(c)).ToArray()).Trim().Trim('\'');
                if (baseName.Length == 0) baseName = "Report";
                baseName = baseName[..Math.Min(31, baseName.Length)];
                var name = baseName; var suffix = 1;
                while (!usedNames.Add(name)) { var tail = $" ({++suffix})"; name = baseName[..Math.Min(baseName.Length, 31 - tail.Length)] + tail; }
                workbookSheets.Add(new XElement(s + "sheet", new XAttribute("name", name), new XAttribute("sheetId", n), new XAttribute(r + "id", $"rId{n}")));
                relationships.Add(new XElement(p + "Relationship", new XAttribute("Id", $"rId{n}"), new XAttribute("Type", r.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{n}.xml")));
            }
            relationships.Add(new XElement(p + "Relationship", new XAttribute("Id", $"rId{sheets.Count + 1}"), new XAttribute("Type", r.NamespaceName + "/styles"), new XAttribute("Target", "styles.xml")));
            void Xml(string path, XElement element) { using var writer = new StreamWriter(target.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(element.ToString(SaveOptions.DisableFormatting)); }
            Xml("[Content_Types].xml", contentTypes!);
            Xml("xl/workbook.xml", new XElement(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), workbookSheets));
            Xml("xl/_rels/workbook.xml.rels", relationships);
        }
        return output.ToArray();
    }
    public static byte[] Create(string title, string user, DateTimeOffset now, string[] headers, JsonElement[][] rows, byte[] logo)
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace p = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            void Xml(string path, XElement element) => Add(path, element.ToString(SaveOptions.DisableFormatting));
            string Col(int i) { string name = ""; for (i++; i > 0; i = (i - 1) / 26) name = (char)('A' + (i - 1) % 26) + name; return name; }
            XElement Cell(int col, int row, string text, int style = 0) => new(s + "c", new XAttribute("r", Col(col) + row), new XAttribute("t", "inlineStr"), new XAttribute("s", style), new XElement(s + "is", new XElement(s + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
            var sheetData = new XElement(s + "sheetData");
            // In invariant mode Windows cannot translate IANA names to Windows IDs.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
            var stamp = TimeZoneInfo.ConvertTime(now, zone).ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture);
            foreach (var (row, text) in new[] { (1, "JCK · " + title), (2, "Downloaded by: " + user), (3, "Downloaded on: " + stamp + " (US Central Time)") })
                sheetData.Add(new XElement(s + "row", new XAttribute("r", row), new XAttribute("ht", 24), new XAttribute("customHeight", 1), Cell(2, row, text, row == 1 ? 1 : 0)));
            sheetData.Add(new XElement(s + "row", new XAttribute("r", 5), headers.Select((h, i) => Cell(i, 5, h, 1))));
            for (int i = 0; i < rows.Length; i++)
            {
                var row = new XElement(s + "row", new XAttribute("r", i + 6));
                for (int j = 0; j < rows[i].Length; j++)
                {
                    var value = rows[i][j];
                    if (value.ValueKind == JsonValueKind.Number)
                        row.Add(new XElement(s + "c", new XAttribute("r", Col(j) + (i + 6)), new XAttribute("s", 2), new XElement(s + "v", value.GetRawText())));
                    else {
                        var text = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString();
                        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) text = date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                        row.Add(Cell(j, i + 6, text));
                    }
                }
                sheetData.Add(row);
            }
            Xml("xl/worksheets/sheet1.xml", new XElement(s + "worksheet", new XAttribute(XNamespace.Xmlns + "r", r),
                new XElement(s + "sheetViews", new XElement(s + "sheetView", new XAttribute("workbookViewId", 0), new XElement(s + "pane", new XAttribute("ySplit", 5), new XAttribute("topLeftCell", "A6"), new XAttribute("state", "frozen")))),
                new XElement(s + "cols", new XElement(s + "col", new XAttribute("min", 1), new XAttribute("max", headers.Length), new XAttribute("width", 25), new XAttribute("customWidth", 1))), sheetData,
                new XElement(s + "autoFilter", new XAttribute("ref", $"A5:{Col(headers.Length - 1)}{Math.Max(5, rows.Length + 5)}")),
                new XElement(s + "drawing", new XAttribute(r + "id", "rId1"))));
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpg" ContentType="image/jpeg"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/></Types>""");
            XElement Rel(string id, string kind, string target) => new(p + "Relationship", new XAttribute("Id", id), new XAttribute("Type", r.NamespaceName + "/" + kind), new XAttribute("Target", target));
            Xml("_rels/.rels", new XElement(p + "Relationships", Rel("rId1", "officeDocument", "xl/workbook.xml")));
            Xml("xl/workbook.xml", new XElement(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), new XElement(s + "sheets", new XElement(s + "sheet", new XAttribute("name", "JCK Report"), new XAttribute("sheetId", 1), new XAttribute(r + "id", "rId1")))));
            Xml("xl/_rels/workbook.xml.rels", new XElement(p + "Relationships", Rel("rId1", "worksheet", "worksheets/sheet1.xml"), Rel("rId2", "styles", "styles.xml")));
            Add("xl/styles.xml", """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="12"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF176B53"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="3"><xf fontId="0" fillId="0" borderId="0" xfId="0"/><xf fontId="1" fillId="2" borderId="0" xfId="0" applyFill="1" applyFont="1"><alignment wrapText="1"/></xf><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""");
            Xml("xl/worksheets/_rels/sheet1.xml.rels", new XElement(p + "Relationships", Rel("rId1", "drawing", "../drawings/drawing1.xml")));
            Xml("xl/drawings/_rels/drawing1.xml.rels", new XElement(p + "Relationships", Rel("rId1", "image", "../media/jck.jpg")));
            Add("xl/drawings/drawing1.xml", """<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="1111250" cy="900000"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="1" name="JCK logo"/><xdr:cNvPicPr><a:picLocks noChangeAspect="1"/></xdr:cNvPicPr></xdr:nvPicPr><xdr:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="rId1"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>""");
            using var image = zip.CreateEntry("xl/media/jck.jpg").Open(); image.Write(logo);
        }
        return output.ToArray();
    }
}
