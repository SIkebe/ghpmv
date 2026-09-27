namespace Ghpmv.Core.GitHub;

// A bounded executable-document parser, not a schema validator. Any parse failure
// discards the entire allowlist, including names collected before the failure.
internal sealed class GraphQLResponseNames
{
    internal const int MaximumQueryLength = 65_536;
    internal const int MaximumNameLength = 128;
    internal const int MaximumDepth = 64;
    internal const int MaximumTokens = 8_192;
    internal const int MaximumResponseNames = 1_024;

    private readonly string _query;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private int _offset;
    private int _tokens;
    private Token _token;
    private string _name = "";

    private GraphQLResponseNames(string query)
    {
        _query = query;
        Advance();
    }

    internal static HashSet<string> Parse(string query)
    {
        if (query.Length > MaximumQueryLength)
        {
            return new(StringComparer.Ordinal);
        }

        var parser = new GraphQLResponseNames(query);
        if (!parser.Document())
        {
            parser._names.Clear();
        }

        return parser._names;
    }

    private bool Document()
    {
        if (_token == Token.End)
        {
            return false;
        }

        while (_token != Token.End)
        {
            if (_token == Token.LeftBrace)
            {
                if (!SelectionSet(1))
                {
                    return false;
                }
            }
            else if (ReadName(out var definition))
            {
                if (definition == "fragment")
                {
                    if (!ReadName(out var fragment) || fragment == "on"
                        || !ReadName(out var on) || on != "on" || !ReadName(out _)
                        || !Directives(1, constant: false) || !SelectionSet(1))
                    {
                        return false;
                    }
                }
                else if (definition is "query" or "mutation" or "subscription")
                {
                    if (_token == Token.Name)
                    {
                        Advance();
                    }

                    if (!Variables() || !Directives(1, constant: false) || !SelectionSet(1))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private bool Variables()
    {
        if (!Take(Token.LeftParen))
        {
            return true;
        }

        do
        {
            if (!Take(Token.Dollar) || !ReadName(out _) || !Take(Token.Colon) || !Type(1)
                || (Take(Token.Equals) && !Value(1, constant: true))
                || !Directives(1, constant: true))
            {
                return false;
            }
        }
        while (_token != Token.RightParen);

        return Take(Token.RightParen);
    }

    private bool Type(int depth)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        if (Take(Token.LeftBracket))
        {
            if (!Type(depth + 1) || !Take(Token.RightBracket))
            {
                return false;
            }
        }
        else if (!ReadName(out _))
        {
            return false;
        }

        Take(Token.Bang);
        return true;
    }

    private bool SelectionSet(int depth)
    {
        if (depth > MaximumDepth || !Take(Token.LeftBrace))
        {
            return false;
        }

        do
        {
            if (Take(Token.Spread))
            {
                if (_token == Token.Name && _name != "on")
                {
                    Advance();
                    if (!Directives(depth, constant: false))
                    {
                        return false;
                    }
                }
                else
                {
                    if (_token == Token.Name)
                    {
                        Advance();
                        if (!ReadName(out _))
                        {
                            return false;
                        }
                    }

                    if (!Directives(depth, constant: false) || !SelectionSet(depth + 1))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (!ReadName(out var responseName) || (Take(Token.Colon) && !ReadName(out _)))
                {
                    return false;
                }

                _names.Add(responseName);
                if (_names.Count > MaximumResponseNames
                    || !Arguments(depth, constant: false) || !Directives(depth, constant: false)
                    || (_token == Token.LeftBrace && !SelectionSet(depth + 1)))
                {
                    return false;
                }
            }
        }
        while (_token != Token.RightBrace);

        return Take(Token.RightBrace);
    }

    private bool Directives(int depth, bool constant)
    {
        while (Take(Token.At))
        {
            if (!ReadName(out _) || !Arguments(depth, constant))
            {
                return false;
            }
        }

        return true;
    }

    private bool Arguments(int depth, bool constant)
    {
        if (!Take(Token.LeftParen))
        {
            return true;
        }

        do
        {
            if (!ReadName(out _) || !Take(Token.Colon) || !Value(depth + 1, constant))
            {
                return false;
            }
        }
        while (_token != Token.RightParen);

        return Take(Token.RightParen);
    }

    private bool Value(int depth, bool constant)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        if (_token is Token.Name or Token.Number or Token.String)
        {
            Advance();
            return true;
        }

        if (!constant && Take(Token.Dollar))
        {
            return ReadName(out _);
        }

        if (Take(Token.LeftBracket))
        {
            while (_token != Token.RightBracket)
            {
                if (!Value(depth + 1, constant))
                {
                    return false;
                }
            }

            return Take(Token.RightBracket);
        }

        if (Take(Token.LeftBrace))
        {
            while (_token != Token.RightBrace)
            {
                if (!ReadName(out _) || !Take(Token.Colon) || !Value(depth + 1, constant))
                {
                    return false;
                }
            }

            return Take(Token.RightBrace);
        }

        return false;
    }

    private bool ReadName(out string name)
    {
        name = _name;
        return Take(Token.Name);
    }

    private bool Take(Token token)
    {
        if (_token != token)
        {
            return false;
        }

        Advance();
        return true;
    }

    private void Advance()
    {
        _token = Token.Invalid;
        while (_offset < _query.Length)
        {
            var character = _query[_offset];
            if (character is '\t' or ' ' or '\r' or '\n' or ',' or '\uFEFF')
            {
                _offset++;
            }
            else if (character == '#')
            {
                while (_offset < _query.Length && _query[_offset] is not ('\r' or '\n'))
                {
                    _offset++;
                }
            }
            else
            {
                break;
            }
        }

        if (_offset == _query.Length)
        {
            _token = Token.End;
            return;
        }

        if (++_tokens > MaximumTokens)
        {
            return;
        }

        var start = _offset;
        var current = _query[_offset++];
        if (IsNameStart(current))
        {
            while (_offset < _query.Length && IsNamePart(_query[_offset]))
            {
                _offset++;
            }

            if (_offset - start <= MaximumNameLength)
            {
                _name = _query[start.._offset];
                _token = Token.Name;
            }
        }
        else if (current == '-' || char.IsAsciiDigit(current))
        {
            _offset = start;
            if (Number())
            {
                _token = Token.Number;
            }
        }
        else if (current == '"')
        {
            _offset = start;
            if (String())
            {
                _token = Token.String;
            }
        }
        else if (current == '.' && _query.AsSpan(_offset).StartsWith("..", StringComparison.Ordinal))
        {
            _offset += 2;
            _token = Token.Spread;
        }
        else
        {
            _token = current switch
            {
                '!' => Token.Bang,
                '$' => Token.Dollar,
                '(' => Token.LeftParen,
                ')' => Token.RightParen,
                ':' => Token.Colon,
                '=' => Token.Equals,
                '@' => Token.At,
                '[' => Token.LeftBracket,
                ']' => Token.RightBracket,
                '{' => Token.LeftBrace,
                '}' => Token.RightBrace,
                _ => Token.Invalid,
            };
        }
    }

    private bool Number()
    {
        Consume('-');
        if (!Consume('0') && !Digits())
        {
            return false;
        }

        if (Consume('.') && !Digits())
        {
            return false;
        }

        if (Consume('e') || Consume('E'))
        {
            if (!Consume('+'))
            {
                Consume('-');
            }

            if (!Digits())
            {
                return false;
            }
        }

        return _offset == _query.Length || (!IsNamePart(_query[_offset]) && _query[_offset] != '.');
    }

    private bool Digits()
    {
        var start = _offset;
        while (_offset < _query.Length && char.IsAsciiDigit(_query[_offset]))
        {
            _offset++;
        }

        return _offset > start;
    }

    private bool Consume(char character)
    {
        if (_offset == _query.Length || _query[_offset] != character)
        {
            return false;
        }

        _offset++;
        return true;
    }

    private bool String()
    {
        var block = _query.AsSpan(_offset).StartsWith("\"\"\"", StringComparison.Ordinal);
        _offset += block ? 3 : 1;
        while (_offset < _query.Length)
        {
            if (block && _query.AsSpan(_offset).StartsWith("\\\"\"\"", StringComparison.Ordinal))
            {
                _offset += 4;
            }
            else if (block && _query.AsSpan(_offset).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                _offset += 3;
                return true;
            }
            else
            {
                var character = _query[_offset++];
                if (!block && character == '"')
                {
                    return true;
                }

                if ((!block && character is '\r' or '\n')
                    || (character < ' ' && character is not ('\t' or '\r' or '\n')))
                {
                    return false;
                }

                if (char.IsSurrogate(character)
                    && (!char.IsHighSurrogate(character) || _offset == _query.Length
                        || !char.IsLowSurrogate(_query[_offset++])))
                {
                    return false;
                }

                if (!block && character == '\\')
                {
                    if (_offset == _query.Length)
                    {
                        return false;
                    }

                    var escape = _query[_offset++];
                    if (escape == 'u')
                    {
                        if (!UnicodeEscape(out var unicode)
                            || char.IsLowSurrogate(unicode))
                        {
                            return false;
                        }

                        if (char.IsHighSurrogate(unicode)
                            && (!Consume('\\') || !Consume('u')
                                || !UnicodeEscape(out var low) || !char.IsLowSurrogate(low)))
                        {
                            return false;
                        }
                    }
                    else if (escape is not ('"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't'))
                    {
                        return false;
                    }
                }
            }
        }

        return false;
    }

    private bool UnicodeEscape(out char character)
    {
        var value = 0;
        character = default;
        for (var digit = 0; digit < 4; digit++)
        {
            if (_offset == _query.Length || !char.IsAsciiHexDigit(_query[_offset]))
            {
                return false;
            }

            var hex = _query[_offset++];
            value = (value * 16) + (char.IsAsciiDigit(hex) ? hex - '0' : char.ToUpperInvariant(hex) - 'A' + 10);
        }

        character = (char)value;
        return true;
    }

    private static bool IsNameStart(char character) => char.IsAsciiLetter(character) || character == '_';

    private static bool IsNamePart(char character) => IsNameStart(character) || char.IsAsciiDigit(character);

    private enum Token
    {
        Invalid, End, Name, Number, String, Spread, Bang, Dollar,
        LeftParen, RightParen, Colon, Equals, At, LeftBracket, RightBracket, LeftBrace, RightBrace,
    }
}
