using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Append-only NDJSON progress stream, flushed after every line.
    ///
    /// The per-line flush is the whole point. The pipeline HTTP server answers one request at a
    /// time, so nothing can poll the editor while a run is in flight; the host CLI instead tails
    /// this file. A buffered writer would make progress appear only at the end, which defeats it.
    /// Flushing per line also means a run killed by a domain reload or an editor crash leaves a
    /// complete, readable record up to the moment it died.
    ///
    /// A monotonic sequence number lets the host CLI resume a tail after a reconnect without
    /// re-emitting lines it already printed.
    /// </summary>
    public sealed class NdjsonWriter : IDisposable
    {
        private readonly StreamWriter m_Writer;
        private readonly StringBuilder m_Builder = new StringBuilder(512);
        private int m_Sequence;

        /// <param name="path">File to write.</param>
        /// <param name="append">
        /// Continue an existing stream instead of truncating it. This is what a run that survives a
        /// domain reload needs: the segment before the reload wrote real records, and recreating the
        /// file would delete the only account of everything that happened up to the transition.
        /// </param>
        /// <param name="startSequence">
        /// Sequence the previous segment reached. Numbers must never rewind, or a host that tails
        /// this file and de-duplicates by sequence would drop the records after the reload.
        /// </param>
        public NdjsonWriter(string path, bool append = false, int startSequence = 0)
        {
            if (startSequence < 0)
                throw new ArgumentOutOfRangeException(nameof(startSequence), startSequence, "A progress sequence cannot be negative.");

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // When appending, the file itself is the authority on where the numbering stands, not
            // the caller's bookkeeping. A resume ledger records the sequence BEFORE a step runs, so
            // trusting it would rewind past the records that step already wrote and emit duplicate
            // seq values — which silently breaks any reader that de-duplicates by sequence, i.e.
            // the host CLI's tail across a domain reload.
            if (append)
                startSequence = Math.Max(startSequence, LastSequenceIn(path));

            // FileShare.ReadWrite so the host CLI can tail the file while it is being written,
            // and so a second reader never locks the writer out.
            var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            m_Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            m_Sequence = startSequence;
        }

        /// <summary>Number of lines written so far.</summary>
        public int Sequence => m_Sequence;

        /// <summary>
        /// Write one event. <paramref name="fields"/> is written verbatim in order; values are
        /// serialized by <see cref="AppendJsonValue"/>, which handles the small set of types a
        /// progress record actually contains.
        /// </summary>
        public void Write(string type, IReadOnlyList<KeyValuePair<string, object>> fields)
        {
            m_Builder.Clear();
            m_Builder.Append('{');

            m_Builder.Append("\"seq\":").Append(++m_Sequence);
            m_Builder.Append(",\"type\":");
            AppendJsonString(m_Builder, type);

            if (fields != null)
            {
                for (var i = 0; i < fields.Count; i++)
                {
                    m_Builder.Append(',');
                    AppendJsonString(m_Builder, fields[i].Key);
                    m_Builder.Append(':');
                    AppendJsonValue(m_Builder, fields[i].Value);
                }
            }

            m_Builder.Append('}');

            m_Writer.Write(m_Builder.ToString());
            m_Writer.Write('\n');
            m_Writer.Flush();
        }

        /// <summary>
        /// Highest sequence already present in an NDJSON file, or 0 when there is none.
        /// Reads the whole file because a run's progress stream is small (tens of lines) and a
        /// backwards seek would have to handle a partially flushed final line anyway.
        /// </summary>
        private static int LastSequenceIn(string path)
        {
            if (!File.Exists(path))
                return 0;

            var highest = 0;

            using (var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    const string Key = "\"seq\":";
                    var at = line.IndexOf(Key, StringComparison.Ordinal);
                    if (at < 0)
                        continue;

                    at += Key.Length;
                    var end = at;
                    while (end < line.Length && char.IsDigit(line[end]))
                        end++;

                    if (end > at && int.TryParse(line.Substring(at, end - at),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) && seq > highest)
                    {
                        highest = seq;
                    }
                }
            }

            return highest;
        }

        private static void AppendJsonValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    AppendJsonString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case float f:
                    AppendJsonNumber(sb, f);
                    break;
                case double d:
                    AppendJsonNumber(sb, d);
                    break;
                case IReadOnlyList<string> list:
                    sb.Append('[');
                    for (var n = 0; n < list.Count; n++)
                    {
                        if (n > 0) sb.Append(',');
                        AppendJsonString(sb, list[n]);
                    }
                    sb.Append(']');
                    break;
                default:
                    AppendJsonString(sb, value.ToString());
                    break;
            }
        }

        private static void AppendJsonNumber(StringBuilder sb, double value)
        {
            // JSON has no NaN or Infinity. Emitting them produces a file no parser can read, so
            // they become null and the reader sees "unknown" rather than a corrupt stream.
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null");
                return;
            }

            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }

        public void Dispose()
        {
            m_Writer.Flush();
            m_Writer.Dispose();
        }
    }
}
