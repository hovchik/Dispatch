using System.Globalization;

namespace Dispatch.Application.Variables;

/// <summary>
/// Realistic fake data for <c>{{$random…}}</c> placeholders (Postman-compatible names) and schema-driven mocks.
/// Pass a seeded <see cref="Random"/> for repeatable output.
/// </summary>
public sealed class Faker(Random? random = null)
{
    private readonly Random _r = random ?? Random.Shared;

    public static Faker Shared { get; } = new();

    // ---- Word lists ---------------------------------------------------------------------------------

    private static readonly string[] FirstNamesList =
    [
        "Ada", "Alan", "Grace", "Linus", "Margaret", "Ken", "Barbara", "Dennis", "Olivia", "Liam", "Emma", "Noah", "Ava",
        "Elijah", "Sophia", "James", "Isabella", "Lucas", "Mia", "Mason", "Amelia", "Ethan", "Harper", "Logan", "Evelyn",
        "Anna", "David", "Sara", "Aram", "Nina", "Hugo", "Lea", "Mateo", "Yuki", "Priya", "Omar", "Chloe", "Leo", "Zoe"
    ];

    private static readonly string[] LastNamesList =
    [
        "Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Thompson", "Liskov", "Ritchie", "Smith", "Johnson",
        "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Martinez", "Lopez", "Wilson", "Anderson", "Taylor",
        "Moore", "Jackson", "Martin", "Lee", "Walker", "Hall", "Allen", "Young", "King", "Wright", "Scott", "Green",
        "Baker", "Adams", "Nelson", "Carter", "Petrosyan", "Novak", "Silva", "Kim", "Nakamura", "Schmidt", "Rossi"
    ];

    private static readonly string[] PrefixesList = ["Mr.", "Mrs.", "Ms.", "Miss", "Dr."];
    private static readonly string[] SuffixesList = ["Jr.", "Sr.", "I", "II", "III", "IV", "V", "MD", "PhD"];

    private static readonly string[] JobDescriptors = ["Senior", "Lead", "Principal", "Junior", "Chief", "Global", "Regional", "Dynamic", "Direct", "Corporate"];
    private static readonly string[] JobAreas = ["Engineering", "Marketing", "Operations", "Security", "Data", "Quality", "Product", "Research", "Finance", "Support", "Design", "Infrastructure"];
    private static readonly string[] JobTypes = ["Engineer", "Manager", "Analyst", "Architect", "Developer", "Consultant", "Designer", "Specialist", "Director", "Administrator", "Tester", "Coordinator"];

    private static readonly string[] Cities =
    [
        "New York", "London", "Paris", "Berlin", "Tokyo", "Yerevan", "Toronto", "Sydney", "Madrid", "Rome", "Amsterdam",
        "Lisbon", "Dublin", "Prague", "Vienna", "Seoul", "Singapore", "Austin", "Denver", "Seattle", "Boston", "Chicago"
    ];

    private static readonly string[] StreetNames = ["Main", "Oak", "Pine", "Maple", "Cedar", "Elm", "Lake", "Hill", "Park", "Washington", "Sunset", "River", "Church", "Mill"];
    private static readonly string[] StreetSuffixes = ["Street", "Avenue", "Road", "Lane", "Boulevard", "Drive", "Court", "Way", "Place"];

    private static readonly (string Name, string Code)[] Countries =
    [
        ("United States", "US"), ("United Kingdom", "GB"), ("Germany", "DE"), ("France", "FR"), ("Japan", "JP"), ("Armenia", "AM"),
        ("Canada", "CA"), ("Australia", "AU"), ("Spain", "ES"), ("Italy", "IT"), ("Netherlands", "NL"), ("Brazil", "BR"),
        ("India", "IN"), ("South Korea", "KR"), ("Sweden", "SE"), ("Norway", "NO"), ("Poland", "PL"), ("Portugal", "PT")
    ];

    private static readonly string[] States = ["California", "Texas", "New York", "Florida", "Washington", "Oregon", "Colorado", "Illinois", "Ohio", "Georgia"];

    private static readonly string[] CompanyWords = ["Acme", "Globex", "Initech", "Umbrella", "Stark", "Wayne", "Hooli", "Vandelay", "Cyberdyne", "Soylent", "Tyrell", "Aperture", "Nimbus", "Vertex", "Lumen", "Quantum", "Orbit", "Nova"];
    private static readonly string[] CompanySuffixes = ["Inc", "LLC", "Group", "Ltd", "Corp", "and Sons", "Labs", "Systems"];
    private static readonly string[] CatchPhraseAdjectives = ["Adaptive", "Robust", "Seamless", "Scalable", "Distributed", "Integrated", "Proactive", "Secure", "Intuitive", "Resilient"];
    private static readonly string[] CatchPhraseNouns = ["framework", "platform", "solution", "architecture", "workflow", "pipeline", "interface", "paradigm", "toolset", "network"];
    private static readonly string[] BsVerbs = ["streamline", "leverage", "synergize", "empower", "orchestrate", "monetize", "transform", "optimize", "scale", "unify"];
    private static readonly string[] BsNouns = ["experiences", "platforms", "markets", "channels", "infrastructures", "deliverables", "metrics", "communities", "workflows", "architectures"];

    private static readonly string[] ProductAdjectives = ["Small", "Ergonomic", "Rustic", "Intelligent", "Gorgeous", "Incredible", "Fantastic", "Practical", "Sleek", "Awesome", "Handmade", "Refined"];
    private static readonly string[] ProductMaterials = ["Steel", "Wooden", "Concrete", "Plastic", "Cotton", "Granite", "Rubber", "Metal", "Soft", "Fresh", "Frozen", "Bronze"];
    private static readonly string[] ProductNouns = ["Chair", "Car", "Computer", "Keyboard", "Mouse", "Bike", "Ball", "Gloves", "Pants", "Shirt", "Table", "Shoes", "Hat", "Towels", "Soap", "Lamp", "Clock"];
    private static readonly string[] Departments = ["Books", "Movies", "Music", "Games", "Electronics", "Computers", "Home", "Garden", "Tools", "Grocery", "Health", "Beauty", "Toys", "Kids", "Sports", "Outdoors", "Clothing", "Shoes", "Jewelery", "Automotive"];

    private static readonly string[] ColorNames = ["red", "green", "blue", "orange", "purple", "teal", "black", "white", "yellow", "cyan", "magenta", "silver", "gold", "indigo", "violet", "maroon"];

    private static readonly string[] Lorem =
    [
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor",
        "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis", "nostrud",
        "exercitation", "ullamco", "laboris", "nisi", "aliquip", "ex", "ea", "commodo", "consequat", "duis", "aute", "irure",
        "in", "reprehenderit", "voluptate", "velit", "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint",
        "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt", "mollit", "anim", "id", "est"
    ];

    private static readonly string[] Words =
    [
        "apple", "river", "cloud", "signal", "rocket", "garden", "bridge", "pixel", "ocean", "forest", "copper", "falcon",
        "harbor", "lantern", "meadow", "quartz", "summit", "thunder", "velvet", "willow", "anchor", "breeze", "canyon", "delta"
    ];

    private static readonly string[] DomainSuffixes = ["com", "net", "org", "io", "dev", "info", "biz", "co"];
    private static readonly string[] FreeEmailDomains = ["gmail.com", "yahoo.com", "hotmail.com", "outlook.com", "proton.me"];
    private static readonly string[] Protocols = ["http", "https"];

    private static readonly (string Code, string Name, string Symbol)[] Currencies =
    [
        ("USD", "US Dollar", "$"), ("EUR", "Euro", "€"), ("GBP", "Pound Sterling", "£"), ("JPY", "Yen", "¥"), ("AMD", "Armenian Dram", "֏"),
        ("CAD", "Canadian Dollar", "$"), ("AUD", "Australian Dollar", "$"), ("CHF", "Swiss Franc", "CHF"), ("INR", "Indian Rupee", "₹")
    ];

    private static readonly string[] TransactionTypes = ["deposit", "withdrawal", "payment", "invoice"];
    private static readonly string[] AccountNames = ["Checking Account", "Savings Account", "Money Market Account", "Investment Account", "Credit Card Account"];

    private static readonly (string Ext, string Mime, string Type)[] Files =
    [
        ("pdf", "application/pdf", "application"), ("json", "application/json", "application"), ("png", "image/png", "image"),
        ("jpg", "image/jpeg", "image"), ("gif", "image/gif", "image"), ("mp3", "audio/mpeg", "audio"), ("mp4", "video/mp4", "video"),
        ("txt", "text/plain", "text"), ("csv", "text/csv", "text"), ("html", "text/html", "text"), ("zip", "application/zip", "application"),
        ("xml", "application/xml", "application")
    ];

    private static readonly string[] Locales = ["en", "en_US", "en_GB", "de", "fr", "es", "it", "ja", "ko", "hy", "pt_BR", "nl", "sv", "pl"];

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Mobile Safari/537.36"
    ];

    private static readonly string[] Abbreviations = ["TCP", "HTTP", "SDD", "RAM", "GB", "CSS", "SSL", "AGP", "SQL", "FTP", "PCI", "AI", "ADP", "RSS", "XML", "EXE", "COM", "HDD", "THX", "SMTP", "SMS", "USB", "PNG", "SAS", "IB", "SCSI", "JSON", "XSS", "JBOD"];
    private static readonly string[] Adjectives = ["auxiliary", "primary", "back-end", "digital", "open-source", "virtual", "cross-platform", "redundant", "online", "haptic", "multi-byte", "bluetooth", "wireless", "1080p", "neural", "optical", "solid state", "mobile"];
    private static readonly string[] Nouns = ["driver", "protocol", "bandwidth", "panel", "microchip", "program", "port", "card", "array", "interface", "system", "sensor", "firewall", "hard drive", "pixel", "alarm", "feed", "monitor", "application", "transmitter", "bus", "circuit", "capacitor", "matrix"];
    private static readonly string[] Verbs = ["back up", "bypass", "hack", "override", "compress", "copy", "navigate", "index", "connect", "generate", "quantify", "calculate", "synthesize", "input", "transmit", "program", "reboot", "parse"];
    private static readonly string[] IngVerbs = ["backing up", "bypassing", "hacking", "overriding", "compressing", "copying", "navigating", "indexing", "connecting", "generating", "quantifying", "calculating", "synthesizing", "transmitting", "programming", "parsing"];
    private static readonly string[] Animals = ["cat", "dog", "fox", "owl", "lion", "tiger", "bear", "wolf", "horse", "rabbit", "eagle", "dolphin", "panda", "koala", "otter"];
    private static readonly string[] ImageCategories = ["abstract", "animals", "business", "cats", "city", "food", "nightlife", "fashion", "people", "nature", "sports", "technics", "transport"];

    // ---- Registry -----------------------------------------------------------------------------------

    private static readonly Dictionary<string, Func<Faker, string>> Generators = new(StringComparer.Ordinal)
    {
        // Common
        ["$guid"] = f => Guid.NewGuid().ToString(),
        ["$uuid"] = f => Guid.NewGuid().ToString(),
        ["$randomUUID"] = f => Guid.NewGuid().ToString(),
        ["$timestamp"] = _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ["$timestampMs"] = _ => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        ["$isoTimestamp"] = _ => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        ["$date"] = _ => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["$randomInt"] = f => f.Int(0, 1000).ToString(CultureInfo.InvariantCulture),
        ["$randomFloat"] = f => f.Float(0, 1000),
        ["$randomBoolean"] = f => f._r.Next(2) == 0 ? "false" : "true",
        ["$randomString"] = f => f.AlphaNumeric(12),

        // Text, numbers, colors
        ["$randomAlphaNumeric"] = f => f.AlphaNumeric(1).ToLowerInvariant(),
        ["$randomColor"] = f => f.Pick(ColorNames),
        ["$randomHexColor"] = f => $"#{f._r.Next(0x1000000):x6}",
        ["$randomAbbreviation"] = f => f.Pick(Abbreviations),

        // Internet and IP addresses
        ["$randomIP"] = f => f.Ipv4(),
        ["$randomIp"] = f => f.Ipv4(),
        ["$randomIPV6"] = f => string.Join(":", Enumerable.Range(0, 8).Select(_ => f._r.Next(0x10000).ToString("x4"))),
        ["$randomMACAddress"] = f => string.Join(":", Enumerable.Range(0, 6).Select(_ => f._r.Next(256).ToString("x2"))),
        ["$randomPassword"] = f => f.AlphaNumeric(15),
        ["$randomLocale"] = f => f.Pick(Locales),
        ["$randomUserAgent"] = f => f.Pick(UserAgents),
        ["$randomProtocol"] = f => f.Pick(Protocols),
        ["$randomSemver"] = f => $"{f._r.Next(10)}.{f._r.Next(20)}.{f._r.Next(50)}",

        // Names
        ["$randomFirstName"] = f => f.FirstName(),
        ["$randomLastName"] = f => f.LastName(),
        ["$randomFullName"] = f => f.FullName(),
        ["$randomNamePrefix"] = f => f.Pick(PrefixesList),
        ["$randomNameSuffix"] = f => f.Pick(SuffixesList),

        // Profession
        ["$randomJobArea"] = f => f.Pick(JobAreas),
        ["$randomJobDescriptor"] = f => f.Pick(JobDescriptors),
        ["$randomJobTitle"] = f => $"{f.Pick(JobDescriptors)} {f.Pick(JobAreas)} {f.Pick(JobTypes)}",
        ["$randomJobType"] = f => f.Pick(JobTypes),

        // Phone, address and location
        ["$randomPhoneNumber"] = f => f.Phone(),
        ["$randomPhone"] = f => f.Phone(),
        ["$randomPhoneNumberExt"] = f => $"{f._r.Next(1, 99)}-{f.Phone()[3..]}",
        ["$randomCity"] = f => f.Pick(Cities),
        ["$randomStreetName"] = f => $"{f.Pick(StreetNames)} {f.Pick(StreetSuffixes)}",
        ["$randomStreetAddress"] = f => f.StreetAddress(),
        ["$randomCountry"] = f => f.Pick(Countries).Name,
        ["$randomCountryCode"] = f => f.Pick(Countries).Code,
        ["$randomState"] = f => f.Pick(States),
        ["$randomZipCode"] = f => f._r.Next(10000, 99999).ToString(CultureInfo.InvariantCulture),
        ["$randomLatitude"] = f => (f._r.NextDouble() * 180 - 90).ToString("0.0000", CultureInfo.InvariantCulture),
        ["$randomLongitude"] = f => (f._r.NextDouble() * 360 - 180).ToString("0.0000", CultureInfo.InvariantCulture),

        // Images
        ["$randomAvatarImage"] = f => $"https://i.pravatar.cc/150?img={f._r.Next(1, 70)}",
        ["$randomImageUrl"] = f => $"https://picsum.photos/seed/{f.AlphaNumeric(6)}/640/480",
        ["$randomAnimalsImage"] = f => $"https://loremflickr.com/640/480/animals?lock={f._r.Next(1000)}",
        ["$randomCatsImage"] = f => $"https://loremflickr.com/640/480/cats?lock={f._r.Next(1000)}",
        ["$randomFoodImage"] = f => $"https://loremflickr.com/640/480/food?lock={f._r.Next(1000)}",
        ["$randomCityImage"] = f => $"https://loremflickr.com/640/480/city?lock={f._r.Next(1000)}",
        ["$randomPeopleImage"] = f => $"https://loremflickr.com/640/480/people?lock={f._r.Next(1000)}",
        ["$randomNatureImage"] = f => $"https://loremflickr.com/640/480/nature?lock={f._r.Next(1000)}",
        ["$randomAbstractImage"] = f => $"https://loremflickr.com/640/480/abstract?lock={f._r.Next(1000)}",
        ["$randomImageDataUri"] = _ => "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",

        // Finance
        ["$randomBankAccount"] = f => f.Digits(8),
        ["$randomBankAccountName"] = f => f.Pick(AccountNames),
        ["$randomCreditCardMask"] = f => f.Digits(4),
        ["$randomCreditCardNumber"] = f => f.CreditCard(),
        ["$randomBankAccountBic"] = f => $"{f.Letters(4)}{f.Pick(Countries).Code}{f.Letters(2)}",
        ["$randomBankAccountIban"] = f => f.Iban(),
        ["$randomTransactionType"] = f => f.Pick(TransactionTypes),
        ["$randomCurrencyCode"] = f => f.Pick(Currencies).Code,
        ["$randomCurrencyName"] = f => f.Pick(Currencies).Name,
        ["$randomCurrencySymbol"] = f => f.Pick(Currencies).Symbol,
        ["$randomBitcoin"] = f => "1" + f.AlphaNumeric(33),
        ["$randomPrice"] = f => (f._r.Next(100, 100000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture),

        // Business
        ["$randomCompanyName"] = f => f.CompanyName(),
        ["$randomCompanySuffix"] = f => f.Pick(CompanySuffixes),
        ["$randomBs"] = f => $"{f.Pick(BsVerbs)} {f.Pick(CatchPhraseAdjectives).ToLowerInvariant()} {f.Pick(BsNouns)}",
        ["$randomBsAdjective"] = f => f.Pick(CatchPhraseAdjectives).ToLowerInvariant(),
        ["$randomBsBuzz"] = f => f.Pick(BsVerbs),
        ["$randomBsNoun"] = f => f.Pick(BsNouns),
        ["$randomCatchPhrase"] = f => $"{f.Pick(CatchPhraseAdjectives)} {f.Pick(Adjectives)} {f.Pick(CatchPhraseNouns)}",
        ["$randomCatchPhraseAdjective"] = f => f.Pick(CatchPhraseAdjectives),
        ["$randomCatchPhraseDescriptor"] = f => f.Pick(Adjectives),
        ["$randomCatchPhraseNoun"] = f => f.Pick(CatchPhraseNouns),

        // Hacker / tech
        ["$randomAdjective"] = f => f.Pick(Adjectives),
        ["$randomNoun"] = f => f.Pick(Nouns),
        ["$randomVerb"] = f => f.Pick(Verbs),
        ["$randomIngverb"] = f => f.Pick(IngVerbs),
        ["$randomPhrase"] = f => $"If we {f.Pick(Verbs)} the {f.Pick(Nouns)}, we can get to the {f.Pick(Abbreviations)} {f.Pick(Nouns)} through the {f.Pick(Adjectives)} {f.Pick(Abbreviations)} {f.Pick(Nouns)}!",

        // Databases
        ["$randomDatabaseColumn"] = f => f.Pick(new[] { "id", "title", "name", "email", "phone", "token", "group", "category", "password", "comment", "avatar", "status", "createdAt", "updatedAt" }),
        ["$randomDatabaseType"] = f => f.Pick(new[] { "int", "varchar", "text", "date", "datetime", "boolean", "decimal", "uuid", "json", "bigint" }),
        ["$randomDatabaseCollation"] = f => f.Pick(new[] { "utf8_unicode_ci", "utf8_general_ci", "utf8mb4_bin", "ascii_bin", "cp1250_general_ci" }),
        ["$randomDatabaseEngine"] = f => f.Pick(new[] { "InnoDB", "MyISAM", "MEMORY", "CSV", "ARCHIVE" }),

        // Dates
        ["$randomDateFuture"] = f => f.Date(1, 365).ToString("R", CultureInfo.InvariantCulture),
        ["$randomDatePast"] = f => f.Date(-365, -1).ToString("R", CultureInfo.InvariantCulture),
        ["$randomDateRecent"] = f => f.Date(-2, 0).ToString("R", CultureInfo.InvariantCulture),
        ["$randomWeekday"] = f => f.Pick(CultureInfo.InvariantCulture.DateTimeFormat.DayNames),
        ["$randomMonth"] = f => f.Pick(CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m.Length > 0).ToArray()),

        // Domains, emails and usernames
        ["$randomDomainName"] = f => f.DomainName(),
        ["$randomDomainSuffix"] = f => f.Pick(DomainSuffixes),
        ["$randomDomainWord"] = f => f.Pick(Words),
        ["$randomEmail"] = f => f.Email(),
        ["$randomExampleEmail"] = f => $"{f.UserName()}@example.{f.Pick(new[] { "com", "net", "org" })}",
        ["$randomUserName"] = f => f.UserName(),
        ["$randomUrl"] = f => $"https://{f.DomainName()}",

        // Files and directories
        ["$randomFileName"] = f => $"{f.Pick(Words)}_{f.Pick(Words)}.{f.Pick(Files).Ext}",
        ["$randomFileType"] = f => f.Pick(Files).Type,
        ["$randomFileExt"] = f => f.Pick(Files).Ext,
        ["$randomCommonFileName"] = f => $"{f.Pick(Words)}.{f.Pick(new[] { "pdf", "txt", "png", "json", "csv" })}",
        ["$randomCommonFileType"] = f => f.Pick(new[] { "application", "image", "text", "video", "audio" }),
        ["$randomCommonFileExt"] = f => f.Pick(new[] { "pdf", "txt", "png", "json", "csv", "mp4", "mp3" }),
        ["$randomFilePath"] = f => $"/{f.Pick(Words)}/{f.Pick(Words)}/{f.Pick(Words)}.{f.Pick(Files).Ext}",
        ["$randomDirectoryPath"] = f => $"/{f.Pick(Words)}/{f.Pick(Words)}",
        ["$randomMimeType"] = f => f.Pick(Files).Mime,

        // Stores
        ["$randomProduct"] = f => f.Pick(ProductNouns),
        ["$randomProductAdjective"] = f => f.Pick(ProductAdjectives),
        ["$randomProductMaterial"] = f => f.Pick(ProductMaterials),
        ["$randomProductName"] = f => $"{f.Pick(ProductAdjectives)} {f.Pick(ProductMaterials)} {f.Pick(ProductNouns)}",
        ["$randomDepartment"] = f => f.Pick(Departments),

        // Grammar
        ["$randomNoun"] = f => f.Pick(Nouns),
        ["$randomWord"] = f => f.Pick(Words),
        ["$randomWords"] = f => string.Join(" ", Enumerable.Range(0, f._r.Next(2, 5)).Select(_ => f.Pick(Words))),

        // Lorem ipsum
        ["$randomLoremWord"] = f => f.Pick(Lorem),
        ["$randomLoremWords"] = f => f.LoremWords(3),
        ["$randomLoremSentence"] = f => f.Sentence(),
        ["$randomLoremSentences"] = f => string.Join(" ", Enumerable.Range(0, f._r.Next(2, 6)).Select(_ => f.Sentence())),
        ["$randomLoremParagraph"] = f => f.Paragraph(),
        ["$randomLoremParagraphs"] = f => string.Join("\n\n", Enumerable.Range(0, 3).Select(_ => f.Paragraph())),
        ["$randomLoremText"] = f => f.Paragraph(),
        ["$randomLoremSlug"] = f => string.Join("-", Enumerable.Range(0, 3).Select(_ => f.Pick(Lorem))),
        ["$randomLoremLines"] = f => string.Join("\n", Enumerable.Range(0, f._r.Next(1, 5)).Select(_ => f.Sentence())),

        // Misc
        ["$randomAnimal"] = f => f.Pick(Animals),
        ["$randomImageCategory"] = f => f.Pick(ImageCategories),
    };

    /// <summary>Every supported dynamic variable name (without parameters), for autocomplete and documentation.</summary>
    public static IReadOnlyList<string> Names { get; } = Generators.Keys.Concat(
        ["$randomInt(min,max)", "$randomFloat(min,max)", "$randomString(length)", "$randomAlphaNumeric(length)",
         "$randomElement(a,b,c)", "$randomDate(-30,30)", "$randomDigits(length)", "$randomLoremWords(n)", "$randomWords(n)"])
        .Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Generates a value for a dynamic variable such as <c>$randomFirstName</c> or a parameterized one like
    /// <c>$randomInt(1,100)</c>. Returns null for unknown names.
    /// </summary>
    public string? Generate(string name)
    {
        if (!name.StartsWith('$'))
            return null;
        if (Generators.TryGetValue(name, out var generator))
            return generator(this);

        var open = name.IndexOf('(');
        if (open < 0 || !name.EndsWith(')'))
            return null;
        var function = name[..open];
        var args = name[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        int Arg(int i, int fallback) =>
            i < args.Length && int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        double DArg(int i, double fallback) =>
            i < args.Length && double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        return function switch
        {
            "$randomInt" => Int(Arg(0, 0), Arg(1, 1000)).ToString(CultureInfo.InvariantCulture),
            "$randomFloat" => Float(DArg(0, 0), DArg(1, 1000)),
            "$randomString" or "$randomAlphaNumeric" => AlphaNumeric(Math.Clamp(Arg(0, 12), 0, 100_000)),
            "$randomDigits" => Digits(Math.Clamp(Arg(0, 6), 1, 1000)),
            "$randomElement" => args.Length == 0 ? "" : args[_r.Next(args.Length)],
            "$randomDate" => Date(Arg(0, -30), Arg(1, 30)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "$randomLoremWords" => LoremWords(Math.Clamp(Arg(0, 3), 1, 1000)),
            "$randomWords" => string.Join(" ", Enumerable.Range(0, Math.Clamp(Arg(0, 3), 1, 1000)).Select(_ => Pick(Words))),
            _ => null
        };
    }

    // ---- Building blocks (also used by schema-driven mocks) ----------------------------------------

    public T Pick<T>(IReadOnlyList<T> items) => items[_r.Next(items.Count)];

    public int Int(int min, int max) => min >= max ? min : _r.Next(min, max == int.MaxValue ? max : max + 1);

    public string Float(double min, double max) =>
        (min + _r.NextDouble() * Math.Max(0, max - min)).ToString("0.00", CultureInfo.InvariantCulture);

    public string FirstName() => Pick(FirstNamesList);
    public string LastName() => Pick(LastNamesList);
    public string FullName() => $"{FirstName()} {LastName()}";
    public string UserName() => $"{FirstName()}.{LastName()}{_r.Next(100)}".ToLowerInvariant();
    public string DomainName() => $"{Pick(Words)}{Pick(Words)}.{Pick(DomainSuffixes)}";
    public string Email() => $"{UserName()}@{Pick(FreeEmailDomains)}";
    public string Phone() => $"+1-{_r.Next(200, 999)}-{_r.Next(200, 999)}-{_r.Next(1000, 9999)}";
    public string City() => Pick(Cities);
    public string Country() => Pick(Countries).Name;
    public string CountryCode() => Pick(Countries).Code;
    public string StreetAddress() => $"{_r.Next(1, 9999)} {Pick(StreetNames)} {Pick(StreetSuffixes)}";
    public string ZipCode() => _r.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);
    public string CompanyName() => $"{Pick(CompanyWords)} {Pick(CompanySuffixes)}";
    public string ProductName() => $"{Pick(ProductAdjectives)} {Pick(ProductMaterials)} {Pick(ProductNouns)}";
    public string Word() => Pick(Words);
    public string Ipv4() => $"{_r.Next(1, 255)}.{_r.Next(0, 256)}.{_r.Next(0, 256)}.{_r.Next(1, 255)}";
    public string Url() => $"https://{DomainName()}";
    public string CurrencyCode() => Pick(Currencies).Code;
    public string ColorName() => Pick(ColorNames);
    public string Department() => Pick(Departments);
    public string JobTitle() => $"{Pick(JobDescriptors)} {Pick(JobAreas)} {Pick(JobTypes)}";

    public DateTimeOffset Date(int minDays, int maxDays) =>
        DateTimeOffset.UtcNow.AddDays(Int(Math.Min(minDays, maxDays), Math.Max(minDays, maxDays)))
            .AddSeconds(-_r.Next(86400)).AddTicks(-(DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerSecond));

    public string AlphaNumeric(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[_r.Next(alphabet.Length)];
        return new string(chars);
    }

    public string Digits(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('0' + _r.Next(10));
        return new string(chars);
    }

    private string Letters(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('A' + _r.Next(26));
        return new string(chars);
    }

    public string LoremWords(int count) => string.Join(" ", Enumerable.Range(0, count).Select(_ => Pick(Lorem)));

    public string Sentence()
    {
        var words = LoremWords(_r.Next(5, 12));
        return char.ToUpperInvariant(words[0]) + words[1..] + ".";
    }

    public string Paragraph() => string.Join(" ", Enumerable.Range(0, _r.Next(3, 6)).Select(_ => Sentence()));

    /// <summary>A Luhn-valid 16-digit card number (test data only).</summary>
    public string CreditCard()
    {
        var digits = "4" + Digits(14);
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
        }
        return digits + (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>A structurally valid IBAN (mod-97 checksum) for a German-style account.</summary>
    public string Iban()
    {
        var bban = Digits(18);
        var rearranged = bban + "1314" + "00"; // "DE" = 13 14
        var remainder = 0;
        foreach (var c in rearranged)
            remainder = (remainder * 10 + (c - '0')) % 97;
        var check = 98 - remainder;
        return $"DE{check:00}{bban}";
    }
}
