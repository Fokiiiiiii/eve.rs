namespace Eve.Characters;

/// <summary>Made-up names for the creation screen's "random name" button, in the flavour of each race.</summary>
public static class RandomNames
{
    private static readonly Dictionary<int, (string[] First, string[] Last)> Pools = new()
    {
        // Caldari
        [1] = (["Aritsu", "Daemi", "Haato", "Ishiri", "Jorren", "Kaijun", "Katsen", "Kiyora", "Mikashi", "Noura", "Otsen", "Raika", "Sakiru", "Soryn", "Tavik", "Tovil", "Yashiro", "Yorun"],
               ["Aikinen", "Arasai", "Endashi", "Isokawa", "Kaatanen", "Kashuro", "Oniseki", "Pashanen", "Sairento", "Tanaskai", "Urakonen", "Vashinen"]),
        // Minmatar
        [2] = (["Astrid", "Bjarn", "Eirik", "Frode", "Gunnar", "Helka", "Ingrun", "Kolbjorn", "Ragna", "Sigrun", "Torvald", "Ulfar", "Vidar", "Yrsa"],
               ["Arnulf", "Brannvik", "Dagsson", "Eikland", "Fjellstad", "Haldorsen", "Ivarsson", "Nordhaug", "Skallagrim", "Yrjot"]),
        // Amarr
        [4] = (["Abaddon", "Basim", "Farid", "Ishmael", "Jamila", "Kalim", "Layla", "Mikhal", "Nadira", "Rahmiel", "Salim", "Tariq", "Zahra"],
               ["Al-Rashid", "Barakat", "Dashti", "Haddad", "Karimi", "Mansour", "Nasser", "Qureshi", "Sarraf", "Zaidan"]),
        // Gallente
        [8] = (["Amelie", "Baptiste", "Cosette", "Damien", "Etienne", "Gaston", "Helene", "Lucien", "Margaux", "Odette", "Pascal", "Solene", "Theo"],
               ["Beaumont", "Charpentier", "Delacroix", "Fontaine", "Gaudin", "Lefevre", "Marchand", "Moreau", "Rousseau", "Valmont"]),
    };

    /// <summary>A name in the flavour of <paramref name="raceId"/> (any race when unknown); it satisfies <see cref="CharacterNames.Validate"/>.</summary>
    public static string Generate(int raceId, Random random)
    {
        var (first, last) = Pools.TryGetValue(raceId, out var pool) ? pool : Pools[Pools.Keys.ElementAt(random.Next(Pools.Count))];
        return $"{first[random.Next(first.Length)]} {last[random.Next(last.Length)]}";
    }

    public static IEnumerable<int> Races => Pools.Keys;
}
