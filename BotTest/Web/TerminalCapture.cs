using System.Text;

namespace BotTest.Web;

/// <summary>
/// Copies everything written to the console into a numbered line buffer, so the AI Room can show the full terminal
/// output (each decision with its reasoning) without watching the terminal itself.
/// </summary>
public static class TerminalCapture
{
    private const int MaxLines = 6000;

    private static readonly object Sync = new();
    private static readonly LinkedList<TerminalLine> Lines = new();
    private static long _next = 1;
    private static bool _installed;

    public static void Install()
    {
        lock (Sync)
        {
            if (_installed)
                return;
            _installed = true;
        }
        Console.SetOut(new TeeWriter(Console.Out));
    }

    /// <summary>Lines numbered above <paramref name="after"/> (at most <paramref name="max"/>, the latest ones).</summary>
    public static TerminalChunk After(long after, int max = 1500)
    {
        lock (Sync)
        {
            var lines = Lines.Where(line => line.Seq > after).ToList();
            if (lines.Count > max)
                lines = lines.GetRange(lines.Count - max, max);
            return new TerminalChunk(lines, _next - 1);
        }
    }

    public static void Clear()
    {
        lock (Sync)
            Lines.Clear();
    }

    private static void Add(string text)
    {
        lock (Sync)
        {
            Lines.AddLast(new TerminalLine(_next++, DateTimeOffset.Now, text));
            while (Lines.Count > MaxLines)
                Lines.RemoveFirst();
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _pending = new();

        public TeeWriter(TextWriter inner) => _inner = inner;

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            lock (_pending)
            {
                if (value == '\n')
                {
                    Add(_pending.ToString().TrimEnd('\r'));
                    _pending.Clear();
                }
                else
                    _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;
            _inner.Write(value);
            lock (_pending)
            {
                foreach (char c in value)
                {
                    if (c == '\n')
                    {
                        Add(_pending.ToString().TrimEnd('\r'));
                        _pending.Clear();
                    }
                    else
                        _pending.Append(c);
                }
            }
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void Flush() => _inner.Flush();
    }
}

public sealed record TerminalLine(long Seq, DateTimeOffset Timestamp, string Text);

public sealed record TerminalChunk(IReadOnlyList<TerminalLine> Lines, long Last);
