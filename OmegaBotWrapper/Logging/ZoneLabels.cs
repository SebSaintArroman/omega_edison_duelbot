using OmegaBotWrapper.Enum;

namespace OmegaBotWrapper.Logging
{
    /// <summary>Readable names for field zones in duel logs.</summary>
    public static class ZoneLabels
    {
        public static string Get(CardLocation location, int sequence) => location switch
        {
            CardLocation.MonsterZone => sequence switch
            {
                <= 4 => $"M{sequence + 1}",
                5 => "EMZ-L",
                6 => "EMZ-R",
                _ => $"M?{sequence}"
            },
            CardLocation.SpellZone => sequence switch
            {
                <= 4 => $"S{sequence + 1}",
                5 => "Field",
                6 => "PZone-L",
                7 => "PZone-R",
                _ => $"S?{sequence}"
            },
            _ => $"{location} {sequence}"
        };
    }
}
