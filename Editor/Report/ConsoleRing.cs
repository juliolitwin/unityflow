using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityFlow.Editor.Report
{
    /// <summary>One captured console message.</summary>
    public readonly struct ConsoleEntry
    {
        public readonly LogType Type;
        public readonly string Message;
        public readonly string StackTrace;

        /// <summary>Monotonic index, used to slice "everything logged since this step began".</summary>
        public readonly int Index;

        public ConsoleEntry(int index, LogType type, string message, string stackTrace)
        {
            Index = index;
            Type = type;
            Message = message;
            StackTrace = stackTrace;
        }

        public bool IsProblem => Type == LogType.Error || Type == LogType.Exception || Type == LogType.Assert;

        public override string ToString() => $"[{Type}] {Message}";
    }

    /// <summary>
    /// Bounded capture of console output during a run.
    ///
    /// This is what turns "waitFor timed out" into a diagnosis. When a step fails, the report
    /// shows the exceptions logged SINCE THAT STEP STARTED — so a NullReferenceException thrown by
    /// the system under test appears next to the assertion it broke, and the reader goes straight
    /// to the stack frame instead of spending another round trip investigating.
    ///
    /// It is a ring buffer because a game can log thousands of lines a second and an unbounded
    /// list would turn a long run into an out-of-memory failure.
    ///
    /// Subscribing is scoped by IDisposable rather than left permanently attached: a handler that
    /// outlives its run keeps the whole run object alive and keeps costing every Debug.Log in the
    /// editor.
    /// </summary>
    public sealed class ConsoleRing : IDisposable
    {
        private readonly ConsoleEntry[] m_Entries;
        private readonly object m_Lock = new object();
        private int m_Count;
        private int m_Next;
        private int m_TotalSeen;
        private bool m_Disposed;

        public ConsoleRing(int capacity = 512)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            m_Entries = new ConsoleEntry[capacity];

            // logMessageReceivedThreaded would also catch logs from background threads, but it
            // delivers them on those threads; the extra lock complexity is not worth it when the
            // interesting logs (gameplay exceptions) all originate on the main thread.
            Application.logMessageReceived += OnLog;
        }

        /// <summary>Total messages observed, including ones the ring has since overwritten.</summary>
        public int Cursor
        {
            get { lock (m_Lock) return m_TotalSeen; }
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (m_Lock)
            {
                m_Entries[m_Next] = new ConsoleEntry(m_TotalSeen, type, condition, stackTrace);
                m_Next = (m_Next + 1) % m_Entries.Length;
                m_TotalSeen++;
                if (m_Count < m_Entries.Length)
                    m_Count++;
            }
        }

        /// <summary>
        /// Everything captured at or after <paramref name="sinceCursor"/>, oldest first.
        /// Entries the ring has already overwritten are simply absent — the report says how many
        /// were dropped rather than pretending it saw them.
        /// </summary>
        public List<ConsoleEntry> Since(int sinceCursor, bool problemsOnly = false)
        {
            var result = new List<ConsoleEntry>();

            lock (m_Lock)
            {
                var start = (m_Next - m_Count + m_Entries.Length) % m_Entries.Length;
                for (var i = 0; i < m_Count; i++)
                {
                    var entry = m_Entries[(start + i) % m_Entries.Length];
                    if (entry.Index < sinceCursor)
                        continue;
                    if (problemsOnly && !entry.IsProblem)
                        continue;

                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// How many messages were logged since <paramref name="sinceCursor"/> but lost to the ring.
        /// Reported so a reader never mistakes a truncated capture for a quiet one.
        /// </summary>
        public int DroppedSince(int sinceCursor)
        {
            lock (m_Lock)
            {
                var oldestRetained = m_TotalSeen - m_Count;
                return oldestRetained > sinceCursor ? oldestRetained - sinceCursor : 0;
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Disposed = true;
            Application.logMessageReceived -= OnLog;
        }
    }
}
