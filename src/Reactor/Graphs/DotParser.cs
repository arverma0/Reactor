using System.Text;

namespace Reactor.Graphs;

/// <summary>A syntax error in DOT source, with a 1-based line and column.</summary>
public sealed class DotParseException : Exception
{
    public DotParseException(string reason, int line, int column)
        : base($"{reason} (line {line}, column {column})")
    {
        Reason = reason;
        Line = line;
        Column = column;
    }

    public string Reason { get; }

    public int Line { get; }

    public int Column { get; }
}

/// <summary>
/// Parser for the strict DOT subset accepted by Reactor (spec section 2): one <c>digraph</c> per file, directed edges only,
/// bare-identifier node ids, typed attribute values, commas between attributes, <c>//</c> and <c>/* */</c> comments,
/// optional semicolons, chained edges, node/edge default blocks, and subgraphs (flattened; used for scoping defaults and
/// deriving stylesheet classes from subgraph labels).
/// </summary>
public static class DotParser
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "graph", "digraph", "node", "edge", "subgraph", "strict" };

    /// <summary>Parses DOT source into a <see cref="Graph"/>.</summary>
    /// <exception cref="DotParseException">The source is not valid in the supported subset.</exception>
    public static Graph Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = Lexer.Tokenize(source);
        var graph = new Parser(tokens).ParseGraph();
        graph.Source = source;
        return graph;
    }

    /// <summary>Reads and parses a <c>.dot</c> file (recording its full path in <see cref="Graph.SourcePath"/>).</summary>
    public static Graph ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var graph = Parse(File.ReadAllText(path));
        graph.SourcePath = Path.GetFullPath(path);
        return graph;
    }

    /// <summary>Derives a stylesheet class from a subgraph label: lowercase, spaces → hyphens, other non-alphanumerics removed.</summary>
    public static string DeriveClassName(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var sb = new StringBuilder(label.Length);
        foreach (var c in label.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }
        }

        return sb.ToString();
    }

    internal static bool IsKeyword(string text) => Keywords.Contains(text);

    private enum TokenKind
    {
        Word,
        String,
        LBrace,
        RBrace,
        LBracket,
        RBracket,
        Equals,
        Comma,
        Semicolon,
        Plus,
        Arrow,
        UndirectedEdge,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column)
    {
        public string Describe() => Kind switch
        {
            TokenKind.End => "end of input",
            TokenKind.String => $"string \"{Text}\"",
            TokenKind.Word => $"'{Text}'",
            _ => $"'{Text}'",
        };
    }

    private static class Lexer
    {
        public static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            var i = 0;
            var line = 1;
            var column = 1;
            var atLineStart = true;

            void Advance(int count = 1)
            {
                for (var k = 0; k < count && i < source.Length; k++)
                {
                    if (source[i] == '\n')
                    {
                        line++;
                        column = 1;
                        atLineStart = true;
                    }
                    else
                    {
                        column++;
                        if (!char.IsWhiteSpace(source[i]))
                        {
                            atLineStart = false;
                        }
                    }

                    i++;
                }
            }

            char PeekChar(int offset = 0) => i + offset < source.Length ? source[i + offset] : '\0';

            while (i < source.Length)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    Advance();
                    continue;
                }

                if (c == '/' && PeekChar(1) == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        Advance();
                    }

                    continue;
                }

                if (c == '/' && PeekChar(1) == '*')
                {
                    var (startLine, startColumn) = (line, column);
                    Advance(2);
                    while (i < source.Length && !(source[i] == '*' && PeekChar(1) == '/'))
                    {
                        Advance();
                    }

                    if (i >= source.Length)
                    {
                        throw new DotParseException("Unterminated block comment", startLine, startColumn);
                    }

                    Advance(2);
                    continue;
                }

                if (c == '#' && atLineStart)
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        Advance();
                    }

                    continue;
                }

                var tokenLine = line;
                var tokenColumn = column;
                switch (c)
                {
                    case '{':
                        tokens.Add(new Token(TokenKind.LBrace, "{", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case '}':
                        tokens.Add(new Token(TokenKind.RBrace, "}", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case '[':
                        tokens.Add(new Token(TokenKind.LBracket, "[", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case ']':
                        tokens.Add(new Token(TokenKind.RBracket, "]", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case '=':
                        tokens.Add(new Token(TokenKind.Equals, "=", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ",", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case ';':
                        tokens.Add(new Token(TokenKind.Semicolon, ";", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case '+':
                        tokens.Add(new Token(TokenKind.Plus, "+", tokenLine, tokenColumn));
                        Advance();
                        continue;
                    case '<':
                        throw new DotParseException("HTML strings/labels are not supported; use a quoted string", tokenLine, tokenColumn);
                    case '"':
                        tokens.Add(new Token(TokenKind.String, ReadString(), tokenLine, tokenColumn));
                        continue;
                }

                if (c == '-' && PeekChar(1) == '>')
                {
                    tokens.Add(new Token(TokenKind.Arrow, "->", tokenLine, tokenColumn));
                    Advance(2);
                    continue;
                }

                if (c == '-' && PeekChar(1) == '-')
                {
                    tokens.Add(new Token(TokenKind.UndirectedEdge, "--", tokenLine, tokenColumn));
                    Advance(2);
                    continue;
                }

                if (IsWordChar(c))
                {
                    var sb = new StringBuilder();
                    while (i < source.Length && IsWordChar(source[i]))
                    {
                        if (source[i] == '-' && (PeekChar(1) == '>' || PeekChar(1) == '-') && sb.Length > 0)
                        {
                            break;
                        }

                        sb.Append(source[i]);
                        Advance();
                    }

                    tokens.Add(new Token(TokenKind.Word, sb.ToString(), tokenLine, tokenColumn));
                    continue;
                }

                throw new DotParseException($"Unexpected character '{c}'", tokenLine, tokenColumn);
            }

            tokens.Add(new Token(TokenKind.End, "", line, column));
            return tokens;

            string ReadString()
            {
                var (startLine, startColumn) = (line, column);
                Advance();
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= source.Length)
                    {
                        throw new DotParseException("Unterminated string", startLine, startColumn);
                    }

                    var ch = source[i];
                    if (ch == '"')
                    {
                        Advance();
                        return sb.ToString();
                    }

                    if (ch == '\\' && i + 1 < source.Length)
                    {
                        var next = source[i + 1];
                        switch (next)
                        {
                            case '"':
                                sb.Append('"');
                                Advance(2);
                                continue;
                            case '\\':
                                sb.Append('\\');
                                Advance(2);
                                continue;
                            case 'n':
                                sb.Append('\n');
                                Advance(2);
                                continue;
                            case 't':
                                sb.Append('\t');
                                Advance(2);
                                continue;
                            case 'r':
                                sb.Append('\r');
                                Advance(2);
                                continue;
                            case '\n':
                                Advance(2);
                                continue;
                            case '\r' when i + 2 < source.Length && source[i + 2] == '\n':
                                Advance(3);
                                continue;
                            default:
                                sb.Append('\\').Append(next);
                                Advance(2);
                                continue;
                        }
                    }

                    sb.Append(ch);
                    Advance();
                }
            }
        }

        private static bool IsWordChar(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-' || c > 127;
    }

    private sealed class Scope
    {
        public Scope(Dictionary<string, string> attributes, Scope? parent, string? name, SubgraphInfo? info = null)
        {
            Attributes = attributes;
            Parent = parent;
            Name = name;
            Info = info;
            NodeDefaults = parent is null ? new(StringComparer.Ordinal) : new(parent.NodeDefaults, StringComparer.Ordinal);
            EdgeDefaults = parent is null ? new(StringComparer.Ordinal) : new(parent.EdgeDefaults, StringComparer.Ordinal);
        }

        public Dictionary<string, string> Attributes { get; }

        public Scope? Parent { get; }

        public string? Name { get; }

        /// <summary>The structural record of this subgraph (null for the root scope).</summary>
        public SubgraphInfo? Info { get; }

        public bool IsRoot => Parent is null;

        public Dictionary<string, string> NodeDefaults { get; }

        public Dictionary<string, string> EdgeDefaults { get; }

        /// <summary>Defaults declared in this scope itself (excluding inherited ones).</summary>
        public Dictionary<string, string> DeclaredNodeDefaults { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> DeclaredEdgeDefaults { get; } = new(StringComparer.Ordinal);

        public List<Node> Members { get; } = [];
    }

    private sealed class Parser(List<Token> tokens)
    {
        private readonly Dictionary<string, Scope> _innermostScope = new(StringComparer.Ordinal);
        private int _position;
        private Graph _graph = null!;

        private Token Peek(int offset = 0) => tokens[Math.Min(_position + offset, tokens.Count - 1)];

        private Token Next() => tokens[Math.Min(_position++, tokens.Count - 1)];

        public Graph ParseGraph()
        {
            var first = Peek();
            if (first.Kind == TokenKind.Word && first.Text.Equals("strict", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(first, "'strict' graphs are not supported");
            }

            if (first.Kind == TokenKind.Word && first.Text.Equals("graph", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(first, "Undirected graphs are not supported; use 'digraph'");
            }

            if (first.Kind != TokenKind.Word || !first.Text.Equals("digraph", StringComparison.OrdinalIgnoreCase))
            {
                throw Error(first, $"Expected 'digraph' but found {first.Describe()}");
            }

            Next();
            var name = "Pipeline";
            if (Peek().Kind is TokenKind.Word or TokenKind.String)
            {
                var nameToken = Next();
                if (nameToken.Kind == TokenKind.Word && IsKeyword(nameToken.Text))
                {
                    throw Error(nameToken, $"'{nameToken.Text}' is a reserved keyword and cannot be used as the graph name");
                }

                name = nameToken.Text;
            }

            Expect(TokenKind.LBrace, "'{' to open the graph body");
            _graph = new Graph(name);
            var root = new Scope(_graph.Attributes, parent: null, name: null);
            ParseStatements(root);
            Expect(TokenKind.RBrace, "'}' to close the graph body");
            foreach (var (key, value) in root.DeclaredNodeDefaults)
            {
                _graph.NodeDefaults[key] = value;
            }

            foreach (var (key, value) in root.DeclaredEdgeDefaults)
            {
                _graph.EdgeDefaults[key] = value;
            }

            var trailing = Peek();
            if (trailing.Kind != TokenKind.End)
            {
                throw trailing.Kind == TokenKind.Word && (trailing.Text.Equals("digraph", StringComparison.OrdinalIgnoreCase) ||
                                                          trailing.Text.Equals("graph", StringComparison.OrdinalIgnoreCase) ||
                                                          trailing.Text.Equals("strict", StringComparison.OrdinalIgnoreCase))
                    ? Error(trailing, "Only one graph per file is supported")
                    : Error(trailing, $"Unexpected {trailing.Describe()} after the end of the graph");
            }

            return _graph;
        }

        private void ParseStatements(Scope scope)
        {
            while (Peek().Kind is not (TokenKind.RBrace or TokenKind.End))
            {
                if (Peek().Kind == TokenKind.Semicolon)
                {
                    Next();
                    continue;
                }

                ParseStatement(scope);
                if (Peek().Kind == TokenKind.Semicolon)
                {
                    Next();
                }
            }
        }

        private void ParseStatement(Scope scope)
        {
            var token = Peek();
            if (token.Kind == TokenKind.LBrace)
            {
                ParseSubgraphBody(scope, name: null);
                return;
            }

            if (token.Kind is not (TokenKind.Word or TokenKind.String))
            {
                throw Error(token, $"Unexpected {token.Describe()}; expected a statement");
            }

            if (token.Kind == TokenKind.Word && IsKeyword(token.Text))
            {
                var keyword = token.Text.ToLowerInvariant();
                switch (keyword)
                {
                    case "graph":
                        Next();
                        Merge(scope.Attributes, ParseAttributeBlocks(required: true));
                        return;
                    case "node":
                        Next();
                        var nodeDefaults = ParseAttributeBlocks(required: true);
                        Merge(scope.NodeDefaults, nodeDefaults);
                        Merge(scope.DeclaredNodeDefaults, nodeDefaults);
                        return;
                    case "edge":
                        Next();
                        var edgeDefaults = ParseAttributeBlocks(required: true);
                        Merge(scope.EdgeDefaults, edgeDefaults);
                        Merge(scope.DeclaredEdgeDefaults, edgeDefaults);
                        return;
                    case "subgraph":
                        Next();
                        string? name = null;
                        if (Peek().Kind is TokenKind.Word or TokenKind.String)
                        {
                            name = Next().Text;
                        }

                        if (Peek().Kind != TokenKind.LBrace)
                        {
                            throw Error(Peek(), "Expected '{' after subgraph");
                        }

                        ParseSubgraphBody(scope, name);
                        return;
                    default:
                        throw Error(token, $"Nested '{token.Text}' is not allowed; only one graph per file is supported");
                }
            }

            var idToken = Next();
            if (Peek().Kind == TokenKind.Equals)
            {
                Next();
                var key = ValidateKey(idToken);
                scope.Attributes[key] = ParseValue();
                return;
            }

            var id = ValidateNodeId(idToken);
            if (Peek().Kind is TokenKind.Arrow or TokenKind.UndirectedEdge)
            {
                ParseEdgeStatement(scope, id);
                return;
            }

            var attributes = Peek().Kind == TokenKind.LBracket ? ParseAttributeBlocks(required: false) : [];
            DeclareNode(scope, id, attributes);
        }

        private void ParseEdgeStatement(Scope scope, string firstId)
        {
            var ids = new List<string> { firstId };
            while (Peek().Kind is TokenKind.Arrow or TokenKind.UndirectedEdge)
            {
                var op = Next();
                if (op.Kind == TokenKind.UndirectedEdge)
                {
                    throw Error(op, "Undirected edges ('--') are not supported; use '->'");
                }

                var target = Peek();
                if (target.Kind == TokenKind.LBrace || (target.Kind == TokenKind.Word && target.Text.Equals("subgraph", StringComparison.OrdinalIgnoreCase)))
                {
                    throw Error(target, "Subgraphs as edge endpoints are not supported; declare each edge explicitly");
                }

                if (target.Kind is not (TokenKind.Word or TokenKind.String))
                {
                    throw Error(target, $"Expected a node id after '->' but found {target.Describe()}");
                }

                ids.Add(ValidateNodeId(Next()));
            }

            var attributes = Peek().Kind == TokenKind.LBracket ? ParseAttributeBlocks(required: false) : [];
            foreach (var id in ids)
            {
                EnsureNode(scope, id);
            }

            for (var i = 0; i < ids.Count - 1; i++)
            {
                var edge = new Edge(ids[i], ids[i + 1]);
                foreach (var (key, value) in scope.EdgeDefaults)
                {
                    edge.Attributes[key] = value;
                }

                foreach (var (key, value) in attributes)
                {
                    edge.Attributes[key] = value;
                }

                _graph.AddEdge(edge);
            }
        }

        private void ParseSubgraphBody(Scope parent, string? name)
        {
            Expect(TokenKind.LBrace, "'{' to open the subgraph");
            var info = new SubgraphInfo(name, parent.Info?.Name, _graph.Subgraphs.Count) { ParentIndex = parent.Info?.Index ?? -1 };
            _graph.Subgraphs.Add(info);
            var scope = new Scope(new Dictionary<string, string>(StringComparer.Ordinal), parent, name, info);
            ParseStatements(scope);
            Expect(TokenKind.RBrace, "'}' to close the subgraph");

            foreach (var (key, value) in scope.Attributes)
            {
                info.Attributes[key] = value;
            }

            Merge(info.NodeDefaults, scope.DeclaredNodeDefaults);
            Merge(info.EdgeDefaults, scope.DeclaredEdgeDefaults);
            foreach (var node in scope.Members)
            {
                // Inner subgraphs close first, so the first scope to claim a node is its innermost one.
                if (_innermostScope.TryAdd(node.Id, scope))
                {
                    info.NodeIds.Add(node.Id);
                }
            }

            var label = scope.Attributes.GetValueOrDefault(AttrKeys.Label);
            var derived = string.IsNullOrWhiteSpace(label) ? null : DeriveClassName(label);
            if (derived is { Length: 0 })
            {
                derived = null;
            }

            var scopeKey = derived ?? name;
            foreach (var node in scope.Members)
            {
                if (derived is not null && !node.DerivedClasses.Contains(derived, StringComparer.Ordinal))
                {
                    // Inner subgraphs finish first, so inserting at the front keeps outermost classes first.
                    node.DerivedClasses.Insert(0, derived);
                }

                node.SubgraphScope ??= scopeKey;
            }

            if (!parent.IsRoot)
            {
                foreach (var node in scope.Members)
                {
                    if (!parent.Members.Contains(node))
                    {
                        parent.Members.Add(node);
                    }
                }
            }
        }

        private void DeclareNode(Scope scope, string id, Dictionary<string, string> attributes)
        {
            var node = _graph.GetNode(id);
            if (node is null)
            {
                node = new Node(id);
                foreach (var (key, value) in scope.NodeDefaults)
                {
                    node.Attributes[key] = value;
                }

                _graph.AddNode(node);
            }

            foreach (var (key, value) in attributes)
            {
                node.Attributes[key] = value;
                node.ExplicitKeys.Add(key);
            }

            if (!scope.IsRoot && !scope.Members.Contains(node))
            {
                scope.Members.Add(node);
            }
        }

        private void EnsureNode(Scope scope, string id)
        {
            if (_graph.ContainsNode(id))
            {
                return;
            }

            var node = new Node(id);
            foreach (var (key, value) in scope.NodeDefaults)
            {
                node.Attributes[key] = value;
            }

            _graph.AddNode(node);
            if (!scope.IsRoot)
            {
                scope.Members.Add(node);
            }
        }

        private Dictionary<string, string> ParseAttributeBlocks(bool required)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (required && Peek().Kind != TokenKind.LBracket)
            {
                throw Error(Peek(), $"Expected '[' but found {Peek().Describe()}");
            }

            while (Peek().Kind == TokenKind.LBracket)
            {
                Next();
                while (Peek().Kind != TokenKind.RBracket)
                {
                    var keyToken = Peek();
                    if (keyToken.Kind == TokenKind.End)
                    {
                        throw Error(keyToken, "Unterminated attribute block; expected ']'");
                    }

                    if (keyToken.Kind is not (TokenKind.Word or TokenKind.String))
                    {
                        throw Error(keyToken, $"Expected an attribute name but found {keyToken.Describe()}");
                    }

                    Next();
                    var key = ValidateKey(keyToken);
                    if (Peek().Kind != TokenKind.Equals)
                    {
                        throw Error(Peek(), $"Expected '=' after attribute '{key}'");
                    }

                    Next();
                    attributes[key] = ParseValue();

                    var separator = Peek();
                    if (separator.Kind is TokenKind.Comma or TokenKind.Semicolon)
                    {
                        Next();
                    }
                    else if (separator.Kind != TokenKind.RBracket)
                    {
                        throw Error(separator, separator.Kind is TokenKind.Word or TokenKind.String
                            ? $"Expected ',' between attributes (before {separator.Describe()})"
                            : $"Expected ',' or ']' but found {separator.Describe()}");
                    }
                }

                Next();
            }

            return attributes;
        }

        private string ParseValue()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.String:
                    var sb = new StringBuilder(token.Text);
                    while (Peek().Kind == TokenKind.Plus)
                    {
                        Next();
                        var next = Next();
                        if (next.Kind != TokenKind.String)
                        {
                            throw Error(next, "Expected a quoted string after '+'");
                        }

                        sb.Append(next.Text);
                    }

                    return sb.ToString();
                case TokenKind.Word:
                    if (!AttributeValues.IsUnquotedValue(token.Text))
                    {
                        throw Error(token, $"Invalid unquoted value '{token.Text}'; wrap the value in double quotes");
                    }

                    return token.Text;
                default:
                    throw Error(token, $"Expected a value but found {token.Describe()}");
            }
        }

        private static string ValidateKey(Token token)
        {
            if (!AttributeValues.IsAttributeKey(token.Text))
            {
                throw Error(token, $"Invalid attribute name '{token.Text}'");
            }

            return token.Text;
        }

        private static string ValidateNodeId(Token token)
        {
            if (token.Kind == TokenKind.Word && IsKeyword(token.Text))
            {
                throw Error(token, $"'{token.Text}' is a reserved keyword and cannot be used as a node id");
            }

            if (!AttributeValues.IsIdentifier(token.Text))
            {
                var hint = token.Text.Contains(':', StringComparison.Ordinal)
                    ? " (ports are not supported)"
                    : "; node ids must match [A-Za-z_][A-Za-z0-9_]* (put display text in the label attribute)";
                throw Error(token, $"Invalid node id '{token.Text}'{hint}");
            }

            return token.Text;
        }

        private void Expect(TokenKind kind, string description)
        {
            var token = Peek();
            if (token.Kind != kind)
            {
                throw Error(token, $"Expected {description} but found {token.Describe()}");
            }

            Next();
        }

        private static void Merge(Dictionary<string, string> target, Dictionary<string, string> source)
        {
            foreach (var (key, value) in source)
            {
                target[key] = value;
            }
        }

        private static DotParseException Error(Token token, string message) => new(message, token.Line, token.Column);
    }
}
