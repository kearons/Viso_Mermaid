using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VisioAddIn1
{
    public class MermaidParser
    {
        private const string DefaultDirection = "TD";
        private const string DefaultNodeShape = "rectangle";
        private const string InvalidNodeId = "-";

        private const string NodeIdPattern = @"[A-Za-z0-9_-]+";
        // Updated shape pattern to support (()) and handle quotes inside shapes
        private const string NodeShapePattern =
            @"(?:\[\[(?<db>.*?)\]\]|\[(?<rec>.*?)\]|\(\((?<circ>.*?)\)\)|\((?<rnd>.*?)\)|\{(?<dia>.*?)\}|\>(?<asym>.*?)\<)";

        public class Node
        {
            public string Id { get; set; }
            public string Text { get; set; }
            public string Shape { get; set; }
        }

        public class Connection
        {
            public string FromId { get; set; }
            public string ToId { get; set; }
            public string Label { get; set; }
            public string ArrowType { get; set; }
        }

        public class FlowchartData
        {
            public List<Node> Nodes { get; set; } = new List<Node>();
            public List<Connection> Connections { get; set; } = new List<Connection>();
            public string Direction { get; set; } = DefaultDirection;
        }

        private sealed class ParseState
        {
            private readonly Dictionary<string, string> _nodeTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _nodeShapes = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenConnections = new HashSet<string>(StringComparer.Ordinal);

            public FlowchartData FlowchartData { get; } = new FlowchartData();

            public void UpsertNode(string id, string text, string shape)
            {
                if (string.IsNullOrWhiteSpace(id) || string.Equals(id, InvalidNodeId, StringComparison.Ordinal))
                {
                    return;
                }

                _nodeTexts[id] = string.IsNullOrWhiteSpace(text) ? id : text;
                _nodeShapes[id] = string.IsNullOrWhiteSpace(shape) ? DefaultNodeShape : shape;
            }

            public void EnsureDefaultNode(string id)
            {
                if (!_nodeTexts.ContainsKey(id))
                {
                    _nodeTexts[id] = id;
                    _nodeShapes[id] = DefaultNodeShape;
                }
            }

            public bool TryAddConnection(string fromId, string toId, string label)
            {
                string connectionKey = $"{fromId}->{toId}|{label}";
                return _seenConnections.Add(connectionKey);
            }

            public void FinalizeNodes()
            {
                foreach (var nodeId in _nodeTexts.Keys.Where(IsValidNodeId))
                {
                    FlowchartData.Nodes.Add(new Node
                    {
                        Id = nodeId,
                        Text = _nodeTexts[nodeId],
                        Shape = _nodeShapes[nodeId]
                    });
                }
            }

            private static bool IsValidNodeId(string nodeId)
            {
                return !string.IsNullOrWhiteSpace(nodeId) &&
                       !string.Equals(nodeId, InvalidNodeId, StringComparison.Ordinal);
            }
        }

        private static readonly Regex GraphHeaderRegex = new Regex(
            @"^(?:graph|flowchart)\s+(?<dir>TD|TB|BT|RL|LR)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 匹配行内定义的节点，例如 A 或 A[文本]
        private static readonly Regex NodeRegex = new Regex(
            $@"(?<id>{NodeIdPattern})(?:\s*{NodeShapePattern})?",
            RegexOptions.Compiled);

        // 匹配连接箭头及其标签
        private static readonly Regex ConnectionRegex = new Regex(
            $@"(?<arrow>==+>|--+>|-+>|--+)(?:\s*(?:(?:\|(?<label>[^|]*)\|)|(?:(?:"")(?<label2>[^""]*)(?:""))))?",
            RegexOptions.Compiled);

        public FlowchartData Parse(string mermaidCode)
        {
            var state = new ParseState();
            var lines = GetMeaningfulLines(mermaidCode).ToList();
            if (lines.Count == 0)
            {
                return state.FlowchartData;
            }

            int startIndex = TryParseHeader(lines[0], state.FlowchartData) ? 1 : 0;
            for (int i = startIndex; i < lines.Count; i++)
            {
                string line = lines[i];
                // 核心修复：先找出所有的箭头位置，防止节点匹配误触发
                var arrowMatches = ConnectionRegex.Matches(line).Cast<Match>().ToList();
                
                RegisterNodesInLine(line, state, arrowMatches);
                RegisterConnectionsInLine(line, state, arrowMatches);
            }

            state.FinalizeNodes();
            return state.FlowchartData;
        }

        private IEnumerable<string> GetMeaningfulLines(string mermaidCode)
        {
            if (string.IsNullOrWhiteSpace(mermaidCode))
            {
                yield break;
            }

            // 修复：支持分号 ';' 分隔符，使其能解析单行多指令
            foreach (var rawLine in mermaidCode.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return line;
            }
        }

        private bool TryParseHeader(string line, FlowchartData result)
        {
            var match = GraphHeaderRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            result.Direction = match.Groups["dir"].Value.ToUpperInvariant();
            return true;
        }

        private void RegisterNodesInLine(string line, ParseState state, List<Match> arrowMatches)
        {
            foreach (Match match in NodeRegex.Matches(line))
            {
                // 更加严格的过滤：如果节点 ID 的任何部分落在了箭头的索引范围内，则该匹配无效
                bool isPartOfArrow = arrowMatches.Any(arrow => 
                    match.Index < arrow.Index + arrow.Length && arrow.Index < match.Index + match.Length);
                
                if (isPartOfArrow)
                    continue;

                string id = match.Groups["id"].Value;
                // 只有当节点包含形状定义（例如 A[text]）时才更新 text/shape，否则仅作为 ID 存在
                if (match.Length > id.Length)
                {
                    state.UpsertNode(id, ExtractNodeText(match), DetectShape(match.Value));
                }
            }
        }

        private void RegisterConnectionsInLine(string line, ParseState state, List<Match> arrowMatches)
        {
            // 核心逻辑：找出所有的节点位置和箭头位置
            var nodeMatches = NodeRegex.Matches(line).Cast<Match>()
                .Where(m => !arrowMatches.Any(a => m.Index >= a.Index && m.Index < a.Index + a.Length))
                .ToList();

            if (nodeMatches.Count < 2 || arrowMatches.Count < 1) return;

            foreach (Match arrow in arrowMatches)
            {
                // 寻找物理位置在箭头左侧最近的节点和右侧最近的节点
                var fromNode = nodeMatches.LastOrDefault(n => n.Index < arrow.Index);
                var toNode = nodeMatches.FirstOrDefault(n => n.Index > arrow.Index);

                if (fromNode != null && toNode != null)
                {
                    string fromId = fromNode.Groups["id"].Value;
                    string toId = toNode.Groups["id"].Value;

                    state.EnsureDefaultNode(fromId);
                    state.EnsureDefaultNode(toId);

                    string label = arrow.Groups["label"].Success ? arrow.Groups["label"].Value :
                                   arrow.Groups["label2"].Success ? arrow.Groups["label2"].Value : "";

                    if (state.TryAddConnection(fromId, toId, label))
                    {
                        state.FlowchartData.Connections.Add(new Connection
                        {
                            FromId = fromId,
                            ToId = toId,
                            Label = label.Trim(),
                            ArrowType = arrow.Groups["arrow"].Value // 记录箭头类型
                        });
                    }
                }
            }
        }

        private string ExtractNodeText(Match match)
        {
            string[] groups = { "db", "rec", "circ", "rnd", "dia", "asym" };
            foreach (var g in groups)
            {
                if (match.Groups[g].Success)
                {
                    string val = match.Groups[g].Value.Trim();
                    // 处理换行符：将字面量 \n 或 <br> 转换为系统换行符
                    val = val.Replace("\\n", Environment.NewLine).Replace("<br/>", Environment.NewLine).Replace("<br>", Environment.NewLine);

                    // 移除两侧引号
                    if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                        val = val.Substring(1, val.Length - 2);
                    return val;
                }
            }
            return match.Groups["id"].Value;
        }

        private string DetectShape(string token)
        {
            if (token.Contains("[[") && token.Contains("]]"))
                return "database";

            if (token.Contains("((") && token.Contains("))"))
                return "circle";

            if (token.Contains("{") && token.Contains("}"))
                return "diamond";

            if (token.Contains("(") && token.Contains(")"))
                return "rounded rectangle";

            if (token.Contains(">") && token.Contains("<"))
                return "circle";

            if (token.Contains("[") && token.Contains("]"))
                return "rectangle";

            return DefaultNodeShape;
        }
    }
}
