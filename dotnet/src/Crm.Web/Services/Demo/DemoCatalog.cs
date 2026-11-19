using System.Globalization;
using System.Text;

namespace Crm.Web.Services.Demo;

public enum DemoSegment { WindEnergy, BuildingMaterials, Industry }

public sealed record DemoCompany(string Name, string Short, string Country, string City, DemoSegment Segment);

/// <summary>
/// Hand-written, invented names. None is meant to match a real company; every web address and email uses example.com (RFC 2606)
/// and phone numbers exist only in the reserved fictional ranges (US +1 555 01xx, UK +44 7700 900xxx), so nothing can reach a real person.
/// </summary>
public static class DemoCatalog
{
    public const string Domain = "example.com";

    public static readonly IReadOnlyList<DemoTeam> Teams =
    [
        new("nordics", "Nordics", Guid.Parse("00000000-0000-4000-8000-0000000fd001")),
        new("dach", "DACH & Poland", Guid.Parse("00000000-0000-4000-8000-0000000fd002")),
        new("americas", "Americas & Asia", Guid.Parse("00000000-0000-4000-8000-0000000fd003")),
    ];

    public static readonly IReadOnlyList<DemoUser> Users =
    [
        User("admin", "00000000d001", "Astrid Holm", "Admin", null),
        User("lars", "00000000d002", "Lars Eriksen", "Manager", "nordics"),
        User("katrine", "00000000d003", "Katrine Bruun", "User", "nordics"),
        User("mikkel", "00000000d004", "Mikkel Sorensen", "User", "nordics"),
        User("jonas", "00000000d005", "Jonas Weber", "Manager", "dach"),
        User("anna", "00000000d006", "Anna Kowalska", "User", "dach"),
        User("emily", "00000000d007", "Emily Carter", "Manager", "americas"),
        User("wei", "00000000d008", "Wei Zhang", "User", "americas"),
        User("lise", "00000000d009", "Lise Bech", "User", null),
        User("peter", "00000000d010", "Peter Juhl", "User", "nordics", false),
    ];

    private static DemoUser User(string key, string oidTail, string name, string role, string? team, bool active = true) =>
        new(key, Guid.Parse("00000000-0000-4000-8000-" + oidTail), name, Slug(name, ".") + "@" + Domain, role, team, active);

    /// <summary>Accounts (by company index) owned by the user without a team.</summary>
    public static readonly IReadOnlySet<int> NoTeamAccounts = new HashSet<int> { 2, 15 };

    public static readonly IReadOnlyList<DemoCompany> Companies =
    [
        new("Nordvind Rotor ApS", "Nordvind Rotor", "DK", "Aarhus", DemoSegment.WindEnergy),
        new("Vestkyst Tårnteknik A/S", "Vestkyst Tårnteknik", "DK", "Esbjerg", DemoSegment.WindEnergy),
        new("Kattegat Bladservice ApS", "Kattegat Bladservice", "DK", "Aalborg", DemoSegment.WindEnergy),
        new("Bothnia Vindkraft Komponenter AB", "Bothnia Vindkraft", "SE", "Umeå", DemoSegment.WindEnergy),
        new("Skagerrak Offshore Fastening AB", "Skagerrak Offshore", "SE", "Göteborg", DemoSegment.WindEnergy),
        new("Nordseewind Getriebe GmbH", "Nordseewind Getriebe", "DE", "Bremerhaven", DemoSegment.WindEnergy),
        new("Küstenfeld Rotorblatt GmbH", "Küstenfeld Rotorblatt", "DE", "Emden", DemoSegment.WindEnergy),
        new("Baltyk Wieże Wiatrowe Sp. z o.o.", "Baltyk Wieże", "PL", "Gdańsk", DemoSegment.WindEnergy),
        new("Wielkopolska Łopaty Sp. z o.o.", "Wielkopolska Łopaty", "PL", "Poznań", DemoSegment.WindEnergy),
        new("Prairie Gale Components Inc.", "Prairie Gale", "US", "Des Moines", DemoSegment.WindEnergy),
        new("Lone Mesa Turbine Services LLC", "Lone Mesa Turbine", "US", "Amarillo", DemoSegment.WindEnergy),
        new("Tramontana Pale Eoliche S.r.l.", "Tramontana Pale", "IT", "Taranto", DemoSegment.WindEnergy),
        new("Hailan Fengdian Bearing Co., Ltd.", "Hailan Fengdian", "CN", "Tianjin", DemoSegment.WindEnergy),
        new("Qinghe Wind Tower Co., Ltd.", "Qinghe Wind Tower", "CN", "Nantong", DemoSegment.WindEnergy),

        new("Nordic Lime & Mørtel A/S", "Nordic Lime", "DK", "Odense", DemoSegment.BuildingMaterials),
        new("Østjysk Tegl ApS", "Østjysk Tegl", "DK", "Horsens", DemoSegment.BuildingMaterials),
        new("Fjordbeton A/S", "Fjordbeton", "DK", "Kolding", DemoSegment.BuildingMaterials),
        new("Skärgård Fasadsystem AB", "Skärgård Fasad", "SE", "Malmö", DemoSegment.BuildingMaterials),
        new("Norrland Träskiva AB", "Norrland Träskiva", "SE", "Sundsvall", DemoSegment.BuildingMaterials),
        new("Alpenstein Dämmstoffe GmbH", "Alpenstein", "DE", "München", DemoSegment.BuildingMaterials),
        new("Elbe Klinker Werke GmbH", "Elbe Klinker", "DE", "Hamburg", DemoSegment.BuildingMaterials),
        new("Baltic Façade Systems Sp. z o.o.", "Baltic Façade", "PL", "Gdynia", DemoSegment.BuildingMaterials),
        new("Mazowsze Płyty Budowlane Sp. z o.o.", "Mazowsze Płyty", "PL", "Warszawa", DemoSegment.BuildingMaterials),
        new("Cascade Panelworks Inc.", "Cascade Panelworks", "US", "Portland", DemoSegment.BuildingMaterials),
        new("Great Lakes Masonry Supply LLC", "Great Lakes Masonry", "US", "Chicago", DemoSegment.BuildingMaterials),
        new("Terre Adriatiche Laterizi S.p.A.", "Terre Adriatiche", "IT", "Ravenna", DemoSegment.BuildingMaterials),
        new("Marmi del Lario S.r.l.", "Marmi del Lario", "IT", "Como", DemoSegment.BuildingMaterials),

        new("Jyske Hydraulik ApS", "Jyske Hydraulik", "DK", "Viborg", DemoSegment.Industry),
        new("Sønderjysk Automation A/S", "Sønderjysk Automation", "DK", "Aabenraa", DemoSegment.Industry),
        new("Lolland Pumpeteknik ApS", "Lolland Pumpeteknik", "DK", "Nakskov", DemoSegment.Industry),
        new("Mälardalen Lagerteknik AB", "Mälardalen Lagerteknik", "SE", "Västerås", DemoSegment.Industry),
        new("Rhein Dichtungstechnik GmbH", "Rhein Dichtungstechnik", "DE", "Duisburg", DemoSegment.Industry),
        new("Schwarzwald Antriebstechnik GmbH", "Schwarzwald Antrieb", "DE", "Freiburg", DemoSegment.Industry),
        new("Ruhr Fördertechnik GmbH", "Ruhr Fördertechnik", "DE", "Essen", DemoSegment.Industry),
        new("Silesia Maszyny Przemysłowe Sp. z o.o.", "Silesia Maszyny", "PL", "Katowice", DemoSegment.Industry),
        new("Ohio Valley Gearworks Inc.", "Ohio Valley Gearworks", "US", "Columbus", DemoSegment.Industry),
        new("Gulf Coast Valve & Flow LLC", "Gulf Coast Valve", "US", "Houston", DemoSegment.Industry),
        new("Brianza Meccanica S.r.l.", "Brianza Meccanica", "IT", "Monza", DemoSegment.Industry),
        new("Jiangnan Precision Seals Co., Ltd.", "Jiangnan Precision", "CN", "Suzhou", DemoSegment.Industry),
        new("Pearl River Automation Co., Ltd.", "Pearl River Automation", "CN", "Shenzhen", DemoSegment.Industry),

        // Edge case: a name of 100+ characters (the column holds 120).
        new("Nordisk Vindenergi Komponenter og Tætningssystemer til Havvindmølleparker og Landbaserede Anlæg i Danmark A/S", "Nordisk Vindenergi", "DK", "Frederikshavn", DemoSegment.WindEnergy),
    ];

    // Edge cases, by account key (A + 1-based index in Companies) or by index.
    public const string KeyAccountKey = "A001";
    public const int KeyAccountContacts = 8;
    public const string NoContactsAccountKey = "A023";
    public const int NoIndustryCompany = 39;
    public const int LongNotesCompany = 3;
    public const int LongNameCompany = 40;
    public const int KeyAccountMinOpportunities = 12;

    public const string LongJobTitle = "Senior Director of Global Strategic Sourcing, Supplier Quality and Sustainable Materials Programmes (EMEA and Americas)";

    /// <summary>About 1,500 characters, built from sentences so it reads like real call notes (the column holds 2,000).</summary>
    public static string LongNotes(DemoSegment s)
    {
        var sb = new StringBuilder($"Segment: {SegmentLabel(s)}. Fictitious demo account with long notes. ");
        string[] parts =
        [
            "Annual review: volumes are stable, but the customer wants shorter lead times on repeat orders. ",
            "Purchasing is centralised; technical approval sits with the plant and takes about six weeks. ",
            "Contract renewal is due after the summer; price pressure from two competitors is expected. ",
            "Delivery terms were renegotiated last year after a delayed shipment; keep the account manager informed of any slippage. ",
            "Sample batches are tested in the customer's own lab; results are shared in the quarterly meeting. ",
        ];
        for (var i = 0; sb.Length < 1_450; i++) { sb.Append(parts[i % parts.Length]); }
        return sb.ToString().TrimEnd();
    }

    public static string SegmentLabel(DemoSegment s) => s switch
    {
        DemoSegment.WindEnergy => "Wind energy",
        DemoSegment.BuildingMaterials => "Building materials",
        _ => "Industry",
    };

    /// <summary>The schema has no segment column; the segment is an 'industry' picklist value whose Code is wind / building / industry.</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> IndustryValues =
        [("wind", "Wind energy"), ("building", "Building materials"), ("industry", "Industry")];

    public static string IndustryLabel(DemoSegment s) => IndustryValues[(int)s].Label;

    public static string TeamOfCountry(string country) => country switch
    {
        "DK" or "SE" => "nordics",
        "DE" or "PL" or "IT" => "dach",
        _ => "americas",
    };

    public static readonly IReadOnlyDictionary<string, string[]> Streets = new Dictionary<string, string[]>
    {
        ["DK"] = ["Havnegade", "Industrivej", "Vestergade", "Fabriksvej", "Strandvejen", "Skovvej"],
        ["SE"] = ["Hamngatan", "Industrivägen", "Storgatan", "Verkstadsgatan", "Skeppsbron"],
        ["DE"] = ["Hafenstraße", "Industriestraße", "Bahnhofstraße", "Werftstraße", "Am Güterbahnhof"],
        ["PL"] = ["ul. Portowa", "ul. Przemysłowa", "ul. Fabryczna", "ul. Stoczniowa", "ul. Kolejowa"],
        ["US"] = ["Industrial Blvd", "Commerce Dr", "Harbor Ave", "Mill St", "Railroad Ave"],
        ["IT"] = ["Via dell'Industria", "Via del Porto", "Via delle Fabbriche", "Via Ferrovia", "Via Artigiani"],
        ["CN"] = ["Gongye Road", "Chuangxin Avenue", "Binhai Road", "Keji Road", "Zhongshan Road"],
    };

    public static string PostalCode(string country, DemoRandom rng) => country switch
    {
        "DK" => rng.Range(1000, 9990).ToString(CultureInfo.InvariantCulture),
        "SE" => $"{rng.Range(100, 999)} {rng.Range(10, 99)}",
        "PL" => $"{rng.Range(10, 99)}-{rng.Range(100, 999)}",
        "CN" => rng.Range(200000, 330000).ToString(CultureInfo.InvariantCulture),
        _ => rng.Range(10000, 99899).ToString(CultureInfo.InvariantCulture),
    };

    public static readonly IReadOnlyDictionary<string, (string[] First, string[] Last)> Names = new Dictionary<string, (string[], string[])>
    {
        ["DK"] = (["Mads", "Anders", "Søren", "Line", "Camilla", "Henrik", "Mette", "Jesper", "Louise", "Kasper", "Pernille", "Rasmus"], ["Hansen", "Jensen", "Pedersen", "Kristensen", "Larsen", "Rasmussen", "Thomsen", "Møller", "Christiansen", "Vester"]),
        ["SE"] = (["Erik", "Johan", "Anna", "Karin", "Lars", "Maria", "Oskar", "Sofia", "Elin", "Gustav"], ["Andersson", "Johansson", "Lindberg", "Nyström", "Eriksson", "Berg", "Holmgren", "Sandberg"]),
        ["DE"] = (["Stefan", "Julia", "Matthias", "Katrin", "Florian", "Sabine", "Jens", "Claudia", "Tobias", "Birgit"], ["Müller", "Schneider", "Fischer", "Weber", "Hoffmann", "Becker", "Wagner", "Schulz", "Keller", "Brandt"]),
        ["PL"] = (["Piotr", "Agnieszka", "Tomasz", "Katarzyna", "Marek", "Joanna", "Paweł", "Magdalena", "Krzysztof", "Ewa"], ["Kowalczyk", "Nowak", "Wiśniewski", "Zielińska", "Lewandowski", "Dąbrowska", "Wójcik", "Kamiński"]),
        ["US"] = (["James", "Emily", "Michael", "Sarah", "David", "Laura", "Kevin", "Rachel", "Brian", "Megan"], ["Anderson", "Brooks", "Carter", "Dawson", "Ellis", "Foster", "Grant", "Hayes", "Mercer", "Nolan"]),
        ["IT"] = (["Marco", "Giulia", "Luca", "Francesca", "Paolo", "Elena", "Andrea", "Chiara", "Stefano", "Silvia"], ["Ferrari", "Bianchi", "Romano", "Colombo", "Ricci", "Marino", "Greco", "Conti"]),
        ["CN"] = (["Wei", "Li", "Jun", "Mei", "Hua", "Yan", "Lei", "Xin", "Fang", "Tao"], ["Zhang", "Wang", "Liu", "Chen", "Yang", "Zhao", "Huang", "Zhou"]),
    };

    public static readonly IReadOnlyList<string> JobTitles =
    [
        "Purchasing Manager", "Head of Procurement", "Technical Director", "Plant Manager", "Operations Manager", "Quality Manager",
        "Project Engineer", "Supply Chain Manager", "Sales Director", "Maintenance Lead", "R&D Manager", "Category Buyer",
    ];

    public static readonly IReadOnlyDictionary<DemoSegment, string[]> Scopes = new Dictionary<DemoSegment, string[]>
    {
        [DemoSegment.WindEnergy] =
        [
            "Rotor bearing retrofit", "Blade root sealing programme", "Tower flange coating line", "Gearbox condition monitoring rollout",
            "Offshore bolt tensioning kits", "Nacelle cooling upgrade", "Pitch system spares agreement", "Blade edge protection",
            "Yaw bearing supply", "Cable termination kits", "Lightning protection retrofit",
        ],
        [DemoSegment.BuildingMaterials] =
        [
            "Facade panel framework agreement", "Insulation board supply", "Clinker delivery programme", "Mortar additive pilot",
            "Prefab wall element line", "Roofing membrane tender", "Fire-rated board qualification", "Aerated concrete line upgrade",
            "Window frame sealing system",
        ],
        [DemoSegment.Industry] =
        [
            "Hydraulic power unit refit", "Conveyor drive replacement", "Valve actuator rollout", "Sensor retrofit for line 3",
            "Gearbox overhaul contract", "Precision casting prototype series", "Seal kit annual contract", "Pump station upgrade",
            "Automation cell expansion",
        ],
    };

    public static readonly IReadOnlyList<string> Topics =
    [
        "delivery schedule", "price adjustment", "technical specification", "sample approval", "framework agreement", "lead times", "certification requirements",
    ];

    public static readonly IReadOnlyList<(string Label, int Weight)> LeadSources =
        [("Referral", 25), ("Event", 20), ("Website", 20), ("Outbound", 20), ("Partner", 15)];

    public static readonly IReadOnlyList<(string Label, int Weight)> LostReasons =
        [("Price", 35), ("Competitor", 25), ("No decision", 20), ("Timing", 12), ("No budget", 5), ("Other", 3)];

    public static IReadOnlyList<(string Currency, int Weight)> CurrenciesFor(string country) => country switch
    {
        "DK" => [("DKK", 80), ("EUR", 20)],
        "SE" => [("SEK", 75), ("EUR", 15), ("DKK", 10)],
        "US" => [("USD", 85), ("EUR", 15)],
        "CN" => [("USD", 70), ("EUR", 30)],
        "PL" => [("EUR", 80), ("DKK", 20)],
        _ => [("EUR", 90), ("DKK", 10)],
    };

    /// <summary>ASCII, lower case, parts joined by <paramref name="separator"/>. "Küstenfeld Rotorblatt" becomes "kuestenfeld-rotorblatt".</summary>
    public static string Slug(string text, string separator = "-")
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            switch (ch)
            {
                case 'ä' or 'Ä': sb.Append("ae"); continue;
                case 'ö' or 'Ö' or 'ø' or 'Ø': sb.Append(char.ToLowerInvariant(ch) == 'ö' ? "oe" : "o"); continue;
                case 'ü' or 'Ü': sb.Append("ue"); continue;
                case 'æ' or 'Æ': sb.Append("ae"); continue;
                case 'ß': sb.Append("ss"); continue;
                case 'ł' or 'Ł': sb.Append('l'); continue;
                default: break;
            }
            foreach (var d in ch.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(d);
                }
            }
        }
        var parts = new List<string>();
        var cur = new StringBuilder();
        foreach (var ch in sb.ToString().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { cur.Append(ch); }
            else if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) { parts.Add(cur.ToString()); }
        return string.Join(separator, parts);
    }
}
