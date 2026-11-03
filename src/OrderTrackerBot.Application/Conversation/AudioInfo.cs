namespace OrderTrackerBot.Application.Conversation;

/// <summary>Cheap facts about a downloaded voice note, read from its bytes with no decoder.</summary>
public static class AudioInfo
{
    /// <summary>
    /// The length in seconds of an Ogg Opus file (what WhatsApp voice notes are): the last Ogg page's granule position counts 48 kHz samples.
    /// Null when the bytes are not Ogg or the position is missing; the caller then falls back to the file size.
    /// </summary>
    public static double? OggOpusSeconds(byte[] data)
    {
        if (data.Length < 27 || data[0] != 'O' || data[1] != 'g' || data[2] != 'g' || data[3] != 'S') return null;
        for (var i = data.Length - 27; i >= 0; i--)
        {
            if (data[i] != 'O' || data[i + 1] != 'g' || data[i + 2] != 'g' || data[i + 3] != 'S') continue;
            var granule = BitConverter.ToInt64(data, i + 6);
            return granule > 0 ? granule / 48000.0 : null;
        }
        return null;
    }
}
