namespace Forterro.Intelligence.Api.Extraction;

/// <summary>
/// Schema JSON de la sortie attendue, contraint cote fournisseur.
///
/// Ce n'est pas de la documentation : il est transmis au moteur d'inference, qui
/// contraint le decodage. La sortie NE PEUT PAS etre malformee — pas de « je demande
/// du JSON et j'espere », pas d'analyse d'un texte libre a coups d'expressions
/// regulieres. Ollama accepte un schema dans son champ `format`, vLLM via son
/// decodage guide : le mecanisme differe, la garantie est la meme.
///
/// Il est NEUTRE du fournisseur, et vit donc ici plutot que dans un connecteur.
///
/// Tous les champs sont nullables a dessein : un modele qui ne trouve pas l'IBAN doit
/// pouvoir rendre null. Le rendre obligatoire forcerait l'invention d'une valeur
/// plausible — exactement ce qu'on veut eviter.
/// </summary>
public static class ExtractionSchema
{
    /// <summary>
    /// Version du couple schema + prompt. Un prompt est un CONTRAT, avec la meme
    /// discipline que les contrats d'evenements (ADR 0004) : on ne modifie pas une
    /// version en place, on en cree une nouvelle.
    /// </summary>
    public const string Version = "invoice-extraction.v1";

    public const string Json = """
    {
      "type": "object",
      "properties": {
        "sellerName":      { "type": ["string", "null"] },
        "sellerVatId":     { "type": ["string", "null"] },
        "buyerName":       { "type": ["string", "null"] },
        "buyerVatId":      { "type": ["string", "null"] },
        "currency":        { "type": ["string", "null"], "description": "Code ISO 4217 a 3 lettres, ex. EUR" },
        "debtorIban":      { "type": ["string", "null"], "description": "IBAN sans espaces. null si illisible." },
        "dueDate":         { "type": ["string", "null"], "description": "Date d'echeance au format AAAA-MM-JJ" },
        "totalInclTax":    { "type": ["number", "null"] },
        "confidence":      { "type": "number", "description": "Entre 0 et 1." },
        "lines": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "description":      { "type": ["string", "null"] },
              "quantity":         { "type": ["number", "null"] },
              "unitPriceExclTax": { "type": ["number", "null"] },
              "vatRate":          { "type": ["number", "null"], "description": "Taux decimal, ex. 0.20 pour 20 %" }
            },
            "required": ["description", "quantity", "unitPriceExclTax", "vatRate"]
          }
        }
      },
      "required": ["sellerName", "buyerName", "currency", "debtorIban", "dueDate", "totalInclTax", "confidence", "lines"]
    }
    """;

    /// <summary>
    /// Consigne accompagnant le schema. Deliberement courte : le schema porte deja
    /// la structure, le prompt ne porte que ce qu'un schema ne peut pas exprimer.
    ///
    /// Deux instructions font tout le travail : ne rien inventer, et dire son
    /// incertitude. Un modele qui comble les trous produit une facture plausible
    /// et fausse, ce qui est bien pire qu'une extraction incomplete.
    /// </summary>
    public const string Prompt = """
    Extrais les champs de cette facture fournisseur.

    Regles imperatives :
    - N'invente aucune valeur. Si un champ est illisible ou absent, rends null.
    - Recopie l'IBAN caractere par caractere, sans espaces. En cas de doute sur un
      seul caractere, rends null plutot qu'une approximation.
    - confidence doit refleter ta certitude reelle sur l'ensemble des champs, et
      baisser des qu'un champ est incertain.
    """;
}
