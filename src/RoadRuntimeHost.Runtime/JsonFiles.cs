using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace RoadRuntimeHost.Runtime
{
    internal static class JsonFiles
    {
        public static T Read<T>(string path) where T : class
        {
            for (int attempt = 0; attempt != 3; ++attempt)
            {
                try
                {
                    string json;
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        json = reader.ReadToEnd();
                    return ReadValue<T>(json);
                }
                catch (IOException error)
                {
                    DiagnosticLog.Warn("IO", "json_read_retry", "JSON file is temporarily unavailable; read will be retried", "path", path, "data_type", typeof(T).FullName, "attempt", (attempt + 1).ToString(), "error", error.Message);
                    if (attempt == 2) throw;
                }
            }
            throw new InvalidOperationException("unreachable");
        }

        public static T ReadValue<T>(string json)
        {
            object parsed = new Parser(json).Parse();
            return (T)ConvertValue(parsed, typeof(T), "$");
        }

        private static object ConvertValue(object value, Type target, string path)
        {
            if (value == null)
            {
                if (!target.IsValueType) return null;
                throw new FormatException(path + " must not be null for " + target.FullName);
            }
            if (target == typeof(string))
            {
                string text = value as string;
                if (text == null) throw TypeError(path, "string", value);
                return text;
            }
            if (target == typeof(bool))
            {
                if (!(value is bool)) throw TypeError(path, "boolean", value);
                return value;
            }
            if (target == typeof(int))
            {
                double number = RequireNumber(value, path);
                if (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
                    throw new FormatException(path + " must be a 32-bit integer");
                return (int)number;
            }
            if (target == typeof(float)) return (float)RequireNumber(value, path);
            if (target == typeof(double)) return RequireNumber(value, path);
            if (target.IsArray)
            {
                List<object> items = value as List<object>;
                if (items == null) throw TypeError(path, "array", value);
                Type elementType = target.GetElementType();
                Array result = Array.CreateInstance(elementType, items.Count);
                for (int index = 0; index != items.Count; ++index)
                    result.SetValue(ConvertValue(items[index], elementType, path + "[" + index + "]"), index);
                return result;
            }

            Dictionary<string, object> fields = value as Dictionary<string, object>;
            if (fields == null) throw TypeError(path, "object", value);
            object instance = Activator.CreateInstance(target);
            foreach (FieldInfo field in target.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = SnakeCase(field.Name);
                object fieldValue;
                if (!fields.TryGetValue(name, out fieldValue)) continue;
                field.SetValue(instance, ConvertValue(fieldValue, field.FieldType, path + "." + name));
            }
            return instance;
        }

        private static string SnakeCase(string name)
        {
            StringBuilder result = new StringBuilder(name.Length + 8);
            for (int index = 0; index != name.Length; ++index)
            {
                char value = name[index];
                if (char.IsUpper(value) && index != 0
                    && (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1])
                        || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                    result.Append('_');
                result.Append(char.ToLowerInvariant(value));
            }
            return result.ToString();
        }

        private static double RequireNumber(object value, string path)
        {
            if (!(value is double)) throw TypeError(path, "number", value);
            double number = (double)value;
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException(path + " must be a finite number");
            return number;
        }

        private static FormatException TypeError(string path, string expected, object actual)
        {
            return new FormatException(path + " must be " + expected + ", not " + (actual == null ? "null" : actual.GetType().Name));
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _index;

            public Parser(string text)
            {
                if (text == null) throw new ArgumentNullException("text");
                _text = text;
            }

            public object Parse()
            {
                SkipWhitespace();
                object value = ParseValue();
                SkipWhitespace();
                if (_index != _text.Length) Fail("unexpected trailing content");
                return value;
            }

            private object ParseValue()
            {
                if (_index >= _text.Length) Fail("unexpected end of input");
                char token = _text[_index];
                if (token == '{') return ParseObject();
                if (token == '[') return ParseArray();
                if (token == '"') return ParseString();
                if (token == '-' || (token >= '0' && token <= '9')) return ParseNumber();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                Fail("unexpected token");
                return null;
            }

            private Dictionary<string, object> ParseObject()
            {
                ++_index;
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Take('}')) return result;
                while (true)
                {
                    SkipWhitespace();
                    if (_index >= _text.Length || _text[_index] != '"') Fail("object key must be a string");
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    if (result.ContainsKey(key)) Fail("duplicate object key '" + key + "'");
                    result.Add(key, ParseValue());
                    SkipWhitespace();
                    if (Take('}')) return result;
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private List<object> ParseArray()
            {
                ++_index;
                List<object> result = new List<object>();
                SkipWhitespace();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (Take(']')) return result;
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private string ParseString()
            {
                Expect('"');
                StringBuilder result = new StringBuilder();
                while (_index < _text.Length)
                {
                    char value = _text[_index++];
                    if (value == '"') return result.ToString();
                    if (value < 32) Fail("control character in string");
                    if (value != '\\')
                    {
                        result.Append(value);
                        continue;
                    }
                    if (_index >= _text.Length) Fail("incomplete string escape");
                    char escaped = _text[_index++];
                    switch (escaped)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u': result.Append(ParseUnicodeEscape()); break;
                        default: Fail("invalid string escape"); break;
                    }
                }
                Fail("unterminated string");
                return null;
            }

            private char ParseUnicodeEscape()
            {
                if (_index + 4 > _text.Length) Fail("incomplete unicode escape");
                int value = 0;
                for (int offset = 0; offset != 4; ++offset)
                {
                    char digit = _text[_index++];
                    value <<= 4;
                    if (digit >= '0' && digit <= '9') value += digit - '0';
                    else if (digit >= 'a' && digit <= 'f') value += digit - 'a' + 10;
                    else if (digit >= 'A' && digit <= 'F') value += digit - 'A' + 10;
                    else Fail("invalid unicode escape");
                }
                return (char)value;
            }

            private double ParseNumber()
            {
                int start = _index;
                Take('-');
                if (Take('0'))
                {
                    if (_index < _text.Length && char.IsDigit(_text[_index])) Fail("leading zero in number");
                }
                else RequireDigits();
                if (Take('.')) RequireDigits();
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    RequireDigits();
                }
                double result;
                if (!double.TryParse(_text.Substring(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                    || double.IsNaN(result) || double.IsInfinity(result))
                    Fail("invalid number");
                return result;
            }

            private void RequireDigits()
            {
                int start = _index;
                while (_index < _text.Length && _text[_index] >= '0' && _text[_index] <= '9') ++_index;
                if (_index == start) Fail("number requires a digit");
            }

            private bool Match(string value)
            {
                if (_index + value.Length > _text.Length) return false;
                for (int offset = 0; offset != value.Length; ++offset)
                    if (_text[_index + offset] != value[offset]) return false;
                _index += value.Length;
                return true;
            }

            private bool Take(char value)
            {
                if (_index >= _text.Length || _text[_index] != value) return false;
                ++_index;
                return true;
            }

            private void Expect(char value)
            {
                if (!Take(value)) Fail("expected '" + value + "'");
            }

            private void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char value = _text[_index];
                    if (value != ' ' && value != '\t' && value != '\r' && value != '\n') return;
                    ++_index;
                }
            }

            private void Fail(string message)
            {
                throw new InvalidDataException("JSON parse error at character " + _index + ": " + message);
            }
        }
    }
}
