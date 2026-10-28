using System.Xml;
using System.Xml.Linq;

namespace WeChatNotes;

internal static class MentionMetadata
{
    internal static string[]? Decode(object value)
    {
        try { return Read(RawChatSource.DecodeContent(value, DBNull.Value)); }
        catch (Exception ex) when (ex is System.Text.DecoderFallbackException or ZstdSharp.ZstdException or InvalidDataException) { return null; }
    }
    internal static string[]? Read(string source)
    {
        if (source.Length == 0) return [];
        if (source.Length > 1024 * 1024) return null;
        try
        {
            using var input = XmlReader.Create(new StringReader(source), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var xml = XDocument.Load(input);
            if (xml.Root?.Name != "msgsource") return null;
            var names = xml.Root.Elements("atuserlist").SelectMany(e => e.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToArray();
            return names.Length > 512 || names.Any(n => n.Length > 256) ? null : names;
        }
        catch (XmlException) { return null; }
    }
}
