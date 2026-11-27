using System.Text.RegularExpressions;

namespace DashboardPerso.Services.Import;

/// <summary>
/// Déduit le bénéficiaire d'un libellé bancaire :
/// « ORANGE SA-ORANGE - Orange - Votre abonnement… » → « ORANGE SA-ORANGE » ;
/// « SUPERMARCHE EXEMPLE 25/02 » → « SUPERMARCHE EXEMPLE » ;
/// « C.P.A.M. TOURS 2632400034 » → « C.P.A.M. TOURS ».
/// </summary>
public static partial class PayeeExtractor
{
    public static string Extract(string description)
    {
        var text = LeadingNumber().Replace(description.Split(" - ", 2)[0].Trim(), "");
        // Supprime en fin de libellé les dates, heures et références numériques.
        string previous;
        do
        {
            previous = text;
            text = TrailingNoise().Replace(text, "").Trim();
        } while (text != previous && text.Length > 0);

        if (text.Length == 0) text = description.Trim();
        return text.Length <= 120 ? text : text[..120];
    }

    // « 25/02 », « 25/02/26 », « 12H00 », « 10/1 » (dates tronquées), ou un nombre d'au moins 5 chiffres.
    [GeneratedRegex(@"\s+(\d{1,2}/\d{1,2}(/\d{2,4})?|\d{1,2}H\d{2}|\d{5,}|[A-Z0-9]*\d{6,}[A-Z0-9]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNoise();

    // Référence numérique en tête : « 10000228591 ECHEANCE » → « ECHEANCE ».
    [GeneratedRegex(@"^\d{5,}\s+")]
    private static partial Regex LeadingNumber();
}
