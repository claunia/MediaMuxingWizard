using System.Globalization;
using System.Text;
using MMW.Formats.Mp4.Resources;

namespace MMW.Formats.Mp4.Boxes;

/// <summary>
/// An ISO-BMFF box held in memory. Leaf boxes keep their payload verbatim; container boxes keep the bytes that
/// precede their children (version/flags, sample-entry fields, …) in <see cref="Payload"/> and the children in
/// <see cref="Children"/>.
/// </summary>
public sealed class Box
{
    public Box(string type, byte[]? payload = null, List<Box>? children = null)
    {
        if (type.Length != 4)
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Error_BoxTypeLength, type), nameof(type));
        Type = type;
        Payload = payload ?? [];
        Children = children;
    }

    /// <summary>Four-character type, decoded as Latin-1 (so "©nam" round-trips).</summary>
    public string Type { get; set; }

    /// <summary>16-byte extended type for <c>uuid</c> boxes.</summary>
    public byte[]? UserType { get; init; }

    /// <summary>Leaf payload, or the prefix written before the children of a container.</summary>
    public byte[] Payload { get; set; }

    /// <summary>Child boxes; null for leaf boxes.</summary>
    public List<Box>? Children { get; set; }

    public bool IsContainer => Children is not null;

    public static Encoding Latin1 { get; } = Encoding.Latin1;

    public Box? Find(string type) => Children?.FirstOrDefault(c => c.Type == type);

    public IEnumerable<Box> FindAll(string type) => Children?.Where(c => c.Type == type) ?? [];

    /// <summary>Finds a descendant by a slash separated path, e.g. "mdia/minf/stbl".</summary>
    public Box? FindPath(string path)
    {
        var current = this;
        foreach (var part in path.Split('/'))
        {
            current = current.Find(part);
            if (current is null)
                return null;
        }

        return current;
    }

    /// <summary>Returns the child of <paramref name="type"/>, creating an empty container if missing.</summary>
    public Box GetOrAddContainer(string type, byte[]? prefix = null)
    {
        Children ??= [];
        var child = Find(type);
        if (child is null)
        {
            child = new Box(type, prefix, []);
            Children.Add(child);
        }
        else
        {
            child.Children ??= [];
        }

        return child;
    }

    public void RemoveAll(string type) => Children?.RemoveAll(c => c.Type == type);

    /// <summary>Replaces (or adds) the first child of the same type.</summary>
    public void SetChild(Box box)
    {
        Children ??= [];
        var index = Children.FindIndex(c => c.Type == box.Type);
        if (index >= 0)
            Children[index] = box;
        else
            Children.Add(box);
    }

    /// <summary>Serialized size including header.</summary>
    public long Size
    {
        get
        {
            long body = Payload.Length + (UserType?.Length ?? 0);
            if (Children is not null)
            {
                foreach (var c in Children)
                    body += c.Size;
            }

            return body + 8 > uint.MaxValue ? body + 16 : body + 8;
        }
    }

    public override string ToString() => IsContainer ? $"{Type} [{Children!.Count}]" : $"{Type} ({Payload.Length} bytes)";
}
