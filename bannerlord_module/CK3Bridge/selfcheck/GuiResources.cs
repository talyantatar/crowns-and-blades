using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace CK3Bridge.SelfCheck
{
    /// <summary>
    /// Static checks of the module's Gauntlet resources against the installed game (read-only) and the built DLL
    /// (reflection-only). Gauntlet fails silently on most of these mistakes (a misspelt binding or brush just draws
    /// nothing), so they are pinned here:
    ///  - files load the way Gauntlet loads them (comments ignored; every child of &lt;Brushes&gt; is a brush);
    ///  - prefab and brush file names do not collide with any installed module's (the resource depot merges by name);
    ///  - every Brush="..." exists (ours or Native's), every font of our brushes exists in the install;
    ///  - every embedded custom prefab exists, and every "@Binding" is a [DataSourceProperty] of CK3CardVM.
    /// </summary>
    internal static class GuiResources
    {
        private static readonly Regex BrushName = new Regex("<Brush\\s+Name=\"([^\"]+)\"", RegexOptions.Compiled);

        /// <summary>Prefabs bound to CK3CardVM; CK3IdleScreen and CK3Backdrop bind nothing.</summary>
        private static readonly string[] CardPrefabs = { "CK3CardBlock", "CK3LoadingWindow", "CK3TitleCard" };

        public static void Run(string moduleDir, string bannerlordBin, Action<bool, string> check)
        {
            string root = Path.GetFullPath(Path.Combine(bannerlordBin, "..", ".."));
            string gui = Path.Combine(moduleDir, "GUI");
            string[] ourPrefabs = Directory.GetFiles(Path.Combine(gui, "Prefabs"), "*.xml");
            string[] ourBrushFiles = Directory.GetFiles(Path.Combine(gui, "Brushes"), "*.xml");

            // Name collisions with every installed module (ours excluded; it is junctioned into Modules).
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installedBrushFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nativeBrushes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string module in Directory.GetDirectories(Path.Combine(root, "Modules")))
            {
                if (Path.GetFileName(module).Equals("CK3Bridge", StringComparison.OrdinalIgnoreCase)) continue;
                string prefabs = Path.Combine(module, "GUI", "Prefabs");
                if (Directory.Exists(prefabs))
                    foreach (string f in Directory.GetFiles(prefabs, "*.xml", SearchOption.AllDirectories)) installed.Add(Path.GetFileNameWithoutExtension(f));
                string brushes = Path.Combine(module, "GUI", "Brushes");
                if (!Directory.Exists(brushes)) continue;
                foreach (string f in Directory.GetFiles(brushes, "*.xml"))
                {
                    installedBrushFiles.Add(Path.GetFileNameWithoutExtension(f));
                    foreach (Match m in BrushName.Matches(File.ReadAllText(f))) nativeBrushes.Add(m.Groups[1].Value);
                }
            }
            foreach (string f in ourPrefabs)
                check(!installed.Contains(Path.GetFileNameWithoutExtension(f)), $"prefab name {Path.GetFileName(f)} is unique among installed modules");
            foreach (string f in ourBrushFiles)
                check(!installedBrushFiles.Contains(Path.GetFileNameWithoutExtension(f)), $"brush file name {Path.GetFileName(f)} is unique among installed modules");

            // Our brushes: each child of <Brushes> is a <Brush> with a name; fonts exist in the install.
            var ourBrushes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in ourBrushFiles)
            {
                XmlNode brushesNode = LoadLikeGauntlet(f).SelectSingleNode("Brushes");
                check(brushesNode != null, $"{Path.GetFileName(f)} has a <Brushes> root");
                if (brushesNode == null) continue;
                foreach (XmlNode node in brushesNode.ChildNodes)
                {
                    string name = node.Attributes?["Name"]?.Value;
                    check(node.Name == "Brush" && !string.IsNullOrEmpty(name), $"{Path.GetFileName(f)}: <{node.Name}> is a named Brush");
                    if (name != null) ourBrushes.Add(name);
                    string font = node.Attributes?["Font"]?.Value;
                    if (font != null)
                        check(File.Exists(Path.Combine(root, "GUI", "GauntletUI", "Fonts", font, font + ".fnt")), $"brush {name}: font {font} is installed");
                }
            }

            // Prefabs: structure, brushes, embedded prefabs and bindings.
            HashSet<string> vmProperties = DataSourceProperties(Path.Combine(moduleDir, "bin", "Win64_Shipping_Client", "CK3Bridge.dll"), "CK3Bridge.CK3CardVM");
            check(vmProperties.Count > 0, "CK3CardVM data source properties found");
            var prefabNames = new HashSet<string>(ourPrefabs.Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
            foreach (string f in ourPrefabs)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                XmlNode window = LoadLikeGauntlet(f).SelectSingleNode("Prefab/Window");
                check(window?.FirstChild is XmlElement, $"{name}: Prefab/Window starts with a widget element");
                if (window == null) continue;

                var bindings = new List<string>();
                foreach (XmlElement element in window.SelectNodes(".//*").OfType<XmlElement>())
                {
                    if (element.Name.StartsWith("CK3", StringComparison.Ordinal))
                        check(prefabNames.Contains(element.Name), $"{name}: embedded prefab <{element.Name}> exists");
                    foreach (XmlAttribute attribute in element.Attributes)
                    {
                        if (attribute.Name == "Brush")
                            check(ourBrushes.Contains(attribute.Value) || nativeBrushes.Contains(attribute.Value), $"{name}: brush {attribute.Value} exists");
                        if (attribute.Value.StartsWith("@", StringComparison.Ordinal)) bindings.Add(attribute.Value.Substring(1));
                    }
                }
                if (CardPrefabs.Contains(name))
                {
                    foreach (string binding in bindings.Distinct())
                        check(vmProperties.Contains(binding), $"{name}: @{binding} is a [DataSourceProperty] of CK3CardVM");
                }
                else
                {
                    check(bindings.Count == 0, $"{name}: binds nothing (its view model has no properties)");
                }
            }
            foreach (string card in CardPrefabs) check(prefabNames.Contains(card), $"prefab {card}.xml exists");
            CheckCardLayout(Path.Combine(gui, "Prefabs", "CK3CardBlock.xml"), check);
        }

        /// <summary>Half the width of Gauntlet's 1920x1080 reference.</summary>
        private const int HalfReferenceWidth = 960;

        /// <summary>The Order of Battle formation lists span 15..315 units from each screen edge (Native OrderOfBattle.xml).</summary>
        private const int OrderOfBattleInnerEdge = 315;
        private const int OrderOfBattleClearance = 15;

        /// <summary>
        /// The title card row (PROTOCOL.md §13.6): coat, text column, coat. Column 820, coats 190 at 30 units from it, so the
        /// coats' outer edges (column/2 + margin + coat = 630) stay 15 units clear of the Order of Battle formation lists.
        /// </summary>
        private static void CheckCardLayout(string prefab, Action<bool, string> check)
        {
            XmlNode row = LoadLikeGauntlet(prefab).SelectSingleNode("//ListPanel[@StackLayout.LayoutMethod='HorizontalLeftToRight']/Children");
            List<XmlElement> cells = row?.ChildNodes.OfType<XmlElement>().ToList() ?? new List<XmlElement>();
            check(cells.Count == 3, "CK3CardBlock: the card row is coat, text column, coat");
            if (cells.Count != 3) return;
            int column = Number(cells[1], "SuggestedWidth");
            int leftCoat = Number(cells[0], "SuggestedWidth"), leftMargin = Number(cells[0], "MarginRight");
            int rightCoat = Number(cells[2], "SuggestedWidth"), rightMargin = Number(cells[2], "MarginLeft");
            check(column == 820, $"CK3CardBlock: text column 820 units (is {column})");
            check(leftMargin == 30 && rightMargin == 30, $"CK3CardBlock: coat margins 30 units (are {leftMargin}/{rightMargin})");
            check(leftCoat == rightCoat, "CK3CardBlock: both coats have the same width");
            int outerEdge = column / 2 + Math.Max(leftMargin, rightMargin) + Math.Max(leftCoat, rightCoat);
            check(outerEdge <= HalfReferenceWidth - OrderOfBattleInnerEdge - OrderOfBattleClearance,
                $"CK3CardBlock: coats end at {outerEdge} units from the centre, clear of the Order of Battle lists (<= 630)");
        }

        private static int Number(XmlElement element, string attribute) =>
            int.TryParse(element.GetAttribute(attribute), out int value) ? value : 0;

        /// <summary>The way WidgetPrefab.LoadFrom and BrushFactory.LoadBrushFromFileAux read files: comments ignored.</summary>
        private static XmlDocument LoadLikeGauntlet(string path)
        {
            var doc = new XmlDocument();
            using (XmlReader reader = XmlReader.Create(new StreamReader(path), new XmlReaderSettings { IgnoreComments = true }))
            {
                doc.Load(reader);
            }
            return doc;
        }

        private static HashSet<string> DataSourceProperties(string dllPath, string typeName)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(dllPath)) return names;
            Type type = Assembly.ReflectionOnlyLoadFrom(dllPath).GetType(typeName, false);
            if (type == null) return names;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (CustomAttributeData.GetCustomAttributes(property).Any(a => a.AttributeType.Name == "DataSourceProperty" || a.AttributeType.Name == "DataSourcePropertyAttribute"))
                    names.Add(property.Name);
            return names;
        }
    }
}
