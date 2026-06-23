namespace SimpleDiffusion.Components
{
    /// <summary>
    /// A node in a parsed prompt tree. A node is either a <b>leaf</b> (a single tag with its
    /// emphasis / weight) or a <b>group</b> (a parenthesised set of child nodes, which may
    /// themselves be groups — i.e. groups can nest).
    /// </summary>
    public class PromptNode
    {
        /// <summary>True if this node is a parenthesised group containing <see cref="Children"/>.</summary>
        public bool IsGroup { get; set; }

        /// <summary>
        /// Number of parenthesis layers wrapping this node.
        /// 0 = none, 1 = (..), 2 = ((..)), etc. Negative = bracket emphasis: -1 = [..].
        /// For a group this is the wrapping around the whole group; for the synthetic
        /// root group it is always 0.
        /// </summary>
        public int ParenDepth { get; set; }

        /// <summary>
        /// Explicit numeric weight, e.g. 1.2 from <c>(tag:1.2)</c> or a group weight from
        /// <c>(a, b:1.2)</c>. Null means no explicit weight.
        /// </summary>
        public double? ExplicitWeight { get; set; }

        // --- Leaf only ---

        /// <summary>The raw tag text for a leaf, e.g. "masterpiece".</summary>
        public string Text { get; set; } = "";

        /// <summary>Whether this leaf is an angle-bracket network tag like &lt;lora:name:1.0&gt;.</summary>
        public bool IsNetworkTag { get; set; }

        /// <summary>Whether this leaf is specifically a LoRA tag.</summary>
        public bool IsLora { get; set; }

        /// <summary>True if this node represents a newline (line break) in the prompt, preserved
        /// so rearranging tags doesn't strip the user's line structure.</summary>
        public bool IsLineBreak { get; set; }

        // --- Group only ---

        /// <summary>Ordered child nodes for a group (or the top-level list for the root).</summary>
        public List<PromptNode> Children { get; set; } = new();

        /// <summary>Parent node, used for re-parenting moves. Null for the root.</summary>
        public PromptNode? Parent { get; set; }

        /// <summary>
        /// Stable identifier assigned by the board after parsing, used to correlate this node
        /// with its DOM element for pointer-based drag &amp; drop. Not part of parse/reconstruct.
        /// </summary>
        public string Id { get; set; } = "";
    }

    /// <summary>
    /// Parses Stable Diffusion prompt strings into a tree of <see cref="PromptNode"/> instances
    /// (preserving nested parenthesis groups) and reconstructs valid prompt strings from a tree.
    /// </summary>
    public static class PromptTokenizer
    {
        /// <summary>
        /// Parse a prompt string into a tree. The returned node is a synthetic root group whose
        /// <see cref="PromptNode.Children"/> are the top-level tags / groups in order.
        /// </summary>
        public static PromptNode Parse(string prompt)
        {
            var root = new PromptNode { IsGroup = true, ParenDepth = 0 };
            if (string.IsNullOrWhiteSpace(prompt)) return root;

            foreach (var (text, isBreak) in SplitTopLevelItems(prompt))
            {
                if (isBreak)
                {
                    root.Children.Add(new PromptNode { IsLineBreak = true, Parent = root });
                    continue;
                }

                var node = ParseNode(text);
                if (node != null)
                {
                    node.Parent = root;
                    root.Children.Add(node);
                }
            }

            return root;
        }

        /// <summary>Parse a single comma-free-at-top-level segment into a node (leaf or group).</summary>
        private static PromptNode? ParseNode(string segment)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            // Angle-bracket network tag, kept verbatim.
            if (trimmed.StartsWith("<") && trimmed.EndsWith(">"))
            {
                return new PromptNode
                {
                    Text = trimmed,
                    IsNetworkTag = true,
                    IsLora = trimmed.StartsWith("<lora:", StringComparison.OrdinalIgnoreCase)
                };
            }

            int parenDepth = 0;
            int bracketDepth = 0;
            double? weight = null;
            string inner = trimmed;

            // Peel balanced outer parentheses, capturing a trailing :weight at each level.
            while (inner.StartsWith("(") && FindMatchingClose(inner, '(', ')') == inner.Length - 1)
            {
                parenDepth++;
                inner = inner.Substring(1, inner.Length - 2);

                int colonIdx = FindWeightColon(inner);
                if (colonIdx >= 0)
                {
                    var weightStr = inner.Substring(colonIdx + 1).Trim();
                    if (double.TryParse(weightStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var w))
                    {
                        weight = w;
                        inner = inner.Substring(0, colonIdx).Trim();
                    }
                }
            }

            // Peel balanced outer brackets (bracket emphasis).
            while (inner.StartsWith("[") && FindMatchingClose(inner, '[', ']') == inner.Length - 1)
            {
                bracketDepth++;
                inner = inner.Substring(1, inner.Length - 2);
            }

            int effectiveDepth = parenDepth - bracketDepth;

            var subs = SplitTopLevel(inner);
            // More than one top-level part => this is a group (possibly nested).
            if (subs.Count > 1)
            {
                var group = new PromptNode
                {
                    IsGroup = true,
                    ParenDepth = effectiveDepth,
                    ExplicitWeight = weight
                };
                foreach (var sub in subs)
                {
                    var child = ParseNode(sub);
                    if (child != null)
                    {
                        child.Parent = group;
                        group.Children.Add(child);
                    }
                }

                // Degenerate group with a single surviving child collapses to that child,
                // folding the wrapping depth/weight onto it.
                if (group.Children.Count == 1)
                {
                    var only = group.Children[0];
                    only.ParenDepth += group.ParenDepth;
                    only.ExplicitWeight ??= group.ExplicitWeight;
                    only.Parent = null;
                    return only;
                }

                return group;
            }

            // Single part => leaf.
            string leafText = subs.Count == 1 ? subs[0].Trim() : inner.Trim();
            bool isNetwork = leafText.StartsWith("<") && leafText.EndsWith(">");
            return new PromptNode
            {
                Text = leafText,
                ParenDepth = effectiveDepth,
                ExplicitWeight = weight,
                IsNetworkTag = isNetwork,
                IsLora = isNetwork && leafText.StartsWith("<lora:", StringComparison.OrdinalIgnoreCase)
            };
        }

        /// <summary>Reconstruct a valid prompt string from a parsed tree (root group), preserving
        /// top-level line breaks.</summary>
        public static string Reconstruct(PromptNode root)
        {
            if (root == null || root.Children.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            bool lastWasTag = false;

            foreach (var child in root.Children)
            {
                if (child.IsLineBreak)
                {
                    sb.Append('\n');
                    lastWasTag = false;
                    continue;
                }

                var rendered = RenderNode(child);
                if (string.IsNullOrEmpty(rendered)) continue;

                if (lastWasTag) sb.Append(", ");
                sb.Append(rendered);
                lastWasTag = true;
            }

            var result = sb.ToString();
            if (lastWasTag) result += ", "; // trailing separator so the user can keep typing
            return result;
        }

        private static string RenderNode(PromptNode node)
        {
            if (!node.IsGroup)
            {
                if (node.IsNetworkTag)
                {
                    string netTag = node.Text;
                    if (node.ParenDepth > 0)
                        netTag = WrapWithParens(netTag, node.ParenDepth, node.ExplicitWeight);
                    return netTag;
                }

                string tag = node.Text;
                if (node.ParenDepth > 0)
                {
                    tag = WrapWithParens(tag, node.ParenDepth, node.ExplicitWeight);
                }
                else if (node.ParenDepth < 0)
                {
                    int depth = Math.Abs(node.ParenDepth);
                    for (int i = 0; i < depth; i++) tag = "[" + tag + "]";
                }
                else if (node.ExplicitWeight.HasValue && Math.Abs(node.ExplicitWeight.Value - 1.0) > 0.001)
                {
                    tag = $"({tag}:{Fmt(node.ExplicitWeight.Value)})";
                }
                return tag;
            }

            // Group
            string innerJoined = string.Join(", ", node.Children.Select(RenderNode).Where(p => !string.IsNullOrEmpty(p)));
            if (node.ParenDepth <= 0 && !node.ExplicitWeight.HasValue)
                return innerJoined; // unwrapped (e.g. accidental depth-0 group)

            if (node.ParenDepth < 0)
            {
                string core = innerJoined;
                int depth = Math.Abs(node.ParenDepth);
                for (int i = 0; i < depth; i++) core = "[" + core + "]";
                return core;
            }

            return WrapWithParens(innerJoined, Math.Max(1, node.ParenDepth), node.ExplicitWeight);
        }

        /// <summary>Returns the display label for a leaf chip (text + weight indicator + parens/brackets).</summary>
        public static string GetChipLabel(PromptNode node)
        {
            if (node.IsNetworkTag) return node.Text;

            string label = node.Text;
            if (node.ExplicitWeight.HasValue && Math.Abs(node.ExplicitWeight.Value - 1.0) > 0.001)
            {
                label += $":{Fmt(node.ExplicitWeight.Value)}";
            }

            if (node.ParenDepth > 0)
                label = new string('(', node.ParenDepth) + label + new string(')', node.ParenDepth);
            else if (node.ParenDepth < 0)
            {
                int d = Math.Abs(node.ParenDepth);
                label = new string('[', d) + label + new string(']', d);
            }

            return label;
        }

        // --- Private helpers ---

        private static string Fmt(double v) =>
            v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static string WrapWithParens(string content, int depth, double? weight)
        {
            string core = content;
            if (weight.HasValue && Math.Abs(weight.Value - 1.0) > 0.001)
            {
                core = $"{content}:{Fmt(weight.Value)}";
            }

            core = "(" + core + ")";
            for (int i = 1; i < depth; i++) core = "(" + core + ")";
            return core;
        }

        /// <summary>Split at top-level commas, respecting parentheses, brackets, and angle brackets.</summary>
        public static List<string> SplitTopLevel(string text)
        {
            var result = new List<string>();
            int parenDepth = 0, bracketDepth = 0, angleDepth = 0, lastStart = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '(': parenDepth++; break;
                    case ')': parenDepth = Math.Max(0, parenDepth - 1); break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth = Math.Max(0, bracketDepth - 1); break;
                    case '<': angleDepth++; break;
                    case '>': angleDepth = Math.Max(0, angleDepth - 1); break;
                    case ',' when parenDepth == 0 && bracketDepth == 0 && angleDepth == 0:
                        result.Add(text.Substring(lastStart, i - lastStart));
                        lastStart = i + 1;
                        break;
                }
            }

            if (lastStart < text.Length)
                result.Add(text.Substring(lastStart));

            return result;
        }

        /// <summary>Split at top-level commas AND newlines (respecting parens/brackets/angles).
        /// Each item is a tag segment, or a line break (IsBreak = true).</summary>
        private static List<(string Text, bool IsBreak)> SplitTopLevelItems(string text)
        {
            var result = new List<(string, bool)>();
            int parenDepth = 0, bracketDepth = 0, angleDepth = 0, lastStart = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '(': parenDepth++; break;
                    case ')': parenDepth = Math.Max(0, parenDepth - 1); break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth = Math.Max(0, bracketDepth - 1); break;
                    case '<': angleDepth++; break;
                    case '>': angleDepth = Math.Max(0, angleDepth - 1); break;
                    case ',' when parenDepth == 0 && bracketDepth == 0 && angleDepth == 0:
                        result.Add((text.Substring(lastStart, i - lastStart), false));
                        lastStart = i + 1;
                        break;
                    case '\n' when parenDepth == 0 && bracketDepth == 0 && angleDepth == 0:
                        int end = i;
                        if (end > lastStart && text[end - 1] == '\r') end--; // drop a preceding \r
                        result.Add((text.Substring(lastStart, end - lastStart), false));
                        result.Add(("", true)); // the line break itself
                        lastStart = i + 1;
                        break;
                }
            }

            if (lastStart < text.Length)
                result.Add((text.Substring(lastStart), false));

            return result;
        }

        private static int FindMatchingClose(string text, char open, char close)
        {
            if (text.Length == 0 || text[0] != open) return -1;

            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == open) depth++;
                else if (text[i] == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindWeightColon(string text)
        {
            int parenDepth = 0, bracketDepth = 0, angleDepth = 0, lastColon = -1;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '(': parenDepth++; break;
                    case ')': parenDepth = Math.Max(0, parenDepth - 1); break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth = Math.Max(0, bracketDepth - 1); break;
                    case '<': angleDepth++; break;
                    case '>': angleDepth = Math.Max(0, angleDepth - 1); break;
                    case ':' when parenDepth == 0 && bracketDepth == 0 && angleDepth == 0:
                        lastColon = i;
                        break;
                }
            }

            if (lastColon >= 0 && lastColon < text.Length - 1)
            {
                var after = text.Substring(lastColon + 1).Trim();
                if (double.TryParse(after, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return lastColon;
                }
            }

            return -1;
        }
    }
}
