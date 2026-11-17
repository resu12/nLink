using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Management.Automation;

namespace NLink.Tools
{
    // Only the original writer's built-in ordered dictionaries and scalar
    // runtime values. No custom dictionary/property/equality callbacks.
    internal sealed class LocalFrontierTerminalSnapshot
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Action check;
        private Dictionary<string, PSNoteProperty> returnedProperties;
        private int characters;
        internal LocalFrontierTerminalSnapshot(IDictionary receipt, Action check)
        {
            this.check = check;
            if (receipt == null || receipt.GetType() != typeof(OrderedDictionary) || receipt.Count > 64) throw new InvalidOperationException("frontier_terminal_receipt_shape");
            int artifacts = 0;
            foreach (DictionaryEntry entry in receipt)
            {
                check(); string key = entry.Key as string;
                if (key == null || key.Length == 0 || key.Length > 128 || key.IndexOf('.') >= 0) throw new InvalidOperationException("frontier_terminal_key");
                var nested = entry.Value as IDictionary;
                if (nested == null) Add(key, entry.Value);
                else
                {
                    if (nested.GetType() != typeof(OrderedDictionary) || ++artifacts > 11 || nested.Count != 2 || !nested.Contains("path") || !nested.Contains("sha256"))
                        throw new InvalidOperationException("frontier_terminal_artifact_shape");
                    Add(key + ".path", nested["path"]); Add(key + ".sha256", nested["sha256"]);
                }
            }
        }
        private void Add(string key, object value)
        {
            var wrapped = value as PSObject; if (wrapped != null) value = wrapped.BaseObject;
            var text = value as string;
            if (text != null)
            { if (text.Length > 4096 || (characters += key.Length + text.Length) > 32768) throw new InvalidOperationException("frontier_terminal_text_capacity"); }
            else if (!(value is int) && !(value is long) && !(value is bool)) throw new InvalidOperationException("frontier_terminal_scalar");
            values.Add(key, value);
        }
        internal string Text(string key) { return values[key] as string; }
        internal long Number(string key)
        { object value = values[key]; if (!(value is int) && !(value is long)) throw new InvalidOperationException("frontier_terminal_number"); return Convert.ToInt64(value); }
        internal bool Flag(string key)
        { object value = values[key]; if (!(value is bool)) throw new InvalidOperationException("frontier_terminal_flag"); return (bool)value; }
        internal bool Matches(IDictionary receipt)
        {
            var now = new LocalFrontierTerminalSnapshot(receipt, check);
            if (now.values.Count != values.Count) return false;
            foreach (var item in values) { object value; if (!now.values.TryGetValue(item.Key, out value) || !Object.Equals(item.Value, value)) return false; }
            return true;
        }
        internal bool BindReturn(object receipt) { return ReadReturn(receipt, true); }
        internal bool MatchesReturn(object receipt) { return ReadReturn(receipt, false); }
        private bool ReadReturn(object receipt, bool bind)
        {
            if (!(receipt is PSObject) || ((PSObject)receipt).BaseObject.GetType() != typeof(PSCustomObject)) return false;
            var returned = (PSObject)receipt; var dictionary = new OrderedDictionary();
            var properties = new Dictionary<string, PSNoteProperty>(StringComparer.Ordinal);
            foreach (var item in returned.Properties)
            {
                check();
                if (item.GetType() != typeof(PSNoteProperty) || dictionary.Count == 64) return false;
                PSNoteProperty original;
                if (!bind && (returnedProperties == null || !returnedProperties.TryGetValue(item.Name, out original) || !Object.ReferenceEquals(original, item))) return false;
                properties.Add(item.Name, (PSNoteProperty)item);
                dictionary.Add(item.Name, item.Value);
            }
            if (!Matches(dictionary)) return false;
            if (bind) returnedProperties = properties;
            return true;
        }
    }
}
