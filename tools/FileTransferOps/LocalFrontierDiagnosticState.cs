using System;
using System.Collections.Generic;
using System.Management.Automation;

namespace NLink.Tools
{
    // Relevant original note properties only. Never execute caller script
    // properties or retain a PowerShell callback for a native lifetime check.
    internal sealed class LocalFrontierDiagnosticState
    {
        private readonly List<Field> fields = new List<Field>();
        private readonly List<Sequence> sequences = new List<Sequence>();
        private System.Array eventItems;
        private EventRow[] eventRows;
        private int cells;
        private long units;
        private readonly Action visit;
        internal string[] TerminalCatalog { get; private set; }
        private LocalFrontierDiagnosticState(Action visit) { this.visit = visit; }
        internal static LocalFrontierDiagnosticState Capture(object failure, object candidates, object proof, object index,
            object fullView, object compactView, int fullCount, Action visit)
        {
            var result = new LocalFrontierDiagnosticState(visit);
            result.Values(failure, "Class", "Reason", "Variant");
            object originalEvent = result.Reference(failure, "Event");
            result.Values(originalEvent, "EventName", "LineNumber", "Sequence", "FilePath");
            result.Reference(originalEvent, "RawLine");
            result.Array(candidates, 8192, true);
            result.Values(proof, "UnresolvedCount", "RecoveredCount");
            foreach (object record in result.Array(result.Reference(proof, "UnresolvedCandidateRecords"), 8192, true))
            { result.Reference(record, "Event"); result.Values(record, "Reason", "Variant", "Recovered"); }
            result.CaptureEvents(result.Reference(index, "AllEvents"), fullCount);
            result.Array(result.Reference(index, "LogFiles"), 1, false);
            foreach (object view in new[] { fullView, compactView })
            {
                int start = result.fields.Count;
                result.Values(view, "Mode", "ContractVersion", "CatalogVersion", "CatalogHash", "BoundaryHash", "ArtifactEvidenceVerdict", "StreamComplete");
                if (result.TerminalCatalog == null)
                    result.TerminalCatalog = new[] { (string)result.fields[start].Value, ((int)result.fields[start + 1].Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        (string)result.fields[start + 2].Value, (string)result.fields[start + 3].Value, (string)result.fields[start + 4].Value };
                result.Array(result.Reference(view, "AuthoritativeFamilies"), 64, false);
                foreach (object boundary in result.Array(result.Reference(view, "Boundaries"), 8192, true))
                    result.Values(boundary, "coordinatorInstanceId", "boundaryId", "evidenceLineIndex", "verdict", "decisionKind", "decisionStatus", "boundaryHash", "writer", "writerSequence");
            }
            return result;
        }
        private void Cell()
        { visit(); if (++cells > 65536) throw new InvalidOperationException("frontier_diagnostic_state_capacity"); }
        private static object Unwrap(object value)
        { var wrapped = value as PSObject; return wrapped == null ? value : wrapped.BaseObject; }
        private object Scalar(object value)
        {
            value = Unwrap(value);
            var text = value as string;
            if (text != null)
            {
                if (text.Length > 262144 || text.Length > LocalPublicationInputLimits.ProducerCodeUnits - units)
                    throw new InvalidOperationException("frontier_diagnostic_text_capacity");
                units += text.Length;
            }
            else if (!(value is int) && !(value is long) && !(value is bool)) throw new InvalidOperationException("frontier_diagnostic_scalar");
            return value;
        }
        private object Add(object owner, string name, bool reference)
        {
            Cell(); if (owner == null) throw new InvalidOperationException("frontier_diagnostic_owner");
            var wrapped = PSObject.AsPSObject(owner); var property = wrapped.Properties[name] as PSNoteProperty;
            if (property == null || property.GetType() != typeof(PSNoteProperty)) throw new InvalidOperationException("frontier_diagnostic_note:" + name);
            object value = property.Value;
            fields.Add(new Field(wrapped, property, reference ? value : Scalar(value), reference)); return value;
        }
        private object Reference(object owner, string name) { return Add(owner, name, true); }
        private void Values(object owner, params string[] names) { foreach (string name in names) Add(owner, name, false); }
        // Input-sized identity storage is bounded separately from metadata.
        // Each row retains all three original checks and their visit charges.
        private void CaptureEvents(object value, int fullCount)
        {
            var items = Unwrap(value) as System.Array;
            if (items == null || items.Rank != 1 || items.GetLowerBound(0) != 0 || items.Length > LocalPublicationInputLimits.InputRecords)
                throw new InvalidOperationException("frontier_diagnostic_collection");
            if (items.Length != fullCount) throw new InvalidOperationException("frontier_diagnostic_index");
            eventItems = items; eventRows = new EventRow[items.Length];
            for (int i = 0; i < eventRows.Length; i++)
            {
                visit(); object item = items.GetValue(i);
                if (item == null) throw new InvalidOperationException("frontier_diagnostic_owner");
                var owner = PSObject.AsPSObject(item);
                var raw = EventProperty(owner, "RawLine"); object text = raw.Value;
                var path = EventProperty(owner, "FilePath");
                eventRows[i] = new EventRow(item, owner, raw, path, text, Scalar(path.Value));
            }
        }
        private PSNoteProperty EventProperty(PSObject owner, string name)
        {
            visit(); var property = owner.Properties[name] as PSNoteProperty;
            if (property == null || property.GetType() != typeof(PSNoteProperty))
                throw new InvalidOperationException("frontier_diagnostic_note:" + name);
            return property;
        }
        private object[] Array(object value, long maximum, bool references)
        {
            var list = Unwrap(value) as System.Array;
            if (list == null || list.Rank != 1 || list.GetLowerBound(0) != 0 || list.Length > maximum)
                throw new InvalidOperationException("frontier_diagnostic_collection");
            if (list.Length > 65536 - cells) throw new InvalidOperationException("frontier_diagnostic_state_capacity");
            var copy = new object[list.Length];
            for (int i = 0; i < copy.Length; i++) { Cell(); copy[i] = references ? list.GetValue(i) : Scalar(list.GetValue(i)); }
            sequences.Add(new Sequence(list, copy, references)); return copy;
        }
        internal bool IsCurrent()
        {
            foreach (var field in fields)
            {
                visit();
                if (!Object.ReferenceEquals(field.Owner.Properties[field.Property.Name], field.Property)) return false;
                object value = field.Property.Value;
                if (field.Reference ? !Object.ReferenceEquals(value, field.Value) : !SameScalar(value, field.Value)) return false;
            }
            foreach (var sequence in sequences)
            {
                visit(); if (sequence.Items.Length != sequence.Values.Length) return false;
                for (int i = 0; i < sequence.Values.Length; i++)
                {
                    visit(); object value = sequence.Items.GetValue(i);
                    if (sequence.References ? !Object.ReferenceEquals(value, sequence.Values[i]) : !SameScalar(value, sequence.Values[i])) return false;
                }
            }
            visit(); if (eventItems.Length != eventRows.Length) return false;
            for (int i = 0; i < eventRows.Length; i++)
            {
                var row = eventRows[i];
                visit(); if (!Object.ReferenceEquals(eventItems.GetValue(i), row.Item)) return false;
                visit(); if (!Object.ReferenceEquals(row.Owner.Properties["RawLine"], row.RawProperty) ||
                    !Object.ReferenceEquals(row.RawProperty.Value, row.RawValue)) return false;
                visit(); if (!Object.ReferenceEquals(row.Owner.Properties["FilePath"], row.PathProperty) ||
                    !SameScalar(row.PathProperty.Value, row.PathValue)) return false;
            }
            return true;
        }
        private static bool SameScalar(object value, object expected)
        {
            value = Unwrap(value);
            // Do not dispatch Equals on a replacement caller-defined object.
            return value != null && value.GetType() == expected.GetType() && Object.Equals(value, expected);
        }
        private struct EventRow
        {
            internal readonly object Item, RawValue, PathValue;
            internal readonly PSObject Owner;
            internal readonly PSNoteProperty RawProperty, PathProperty;
            internal EventRow(object item, PSObject owner, PSNoteProperty raw, PSNoteProperty path, object text, object filePath)
            { Item = item; Owner = owner; RawProperty = raw; PathProperty = path; RawValue = text; PathValue = filePath; }
        }
        private sealed class Field
        {
            internal readonly PSObject Owner; internal readonly PSNoteProperty Property;
            internal readonly object Value; internal readonly bool Reference;
            internal Field(PSObject owner, PSNoteProperty property, object value, bool reference)
            { Owner = owner; Property = property; Value = value; Reference = reference; }
        }
        private sealed class Sequence
        {
            internal readonly System.Array Items; internal readonly object[] Values; internal readonly bool References;
            internal Sequence(System.Array items, object[] values, bool references) { Items = items; Values = values; References = references; }
        }
    }
}
