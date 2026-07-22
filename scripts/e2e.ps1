# Scenario de bout en bout : facture -> outbox -> Kafka -> saga -> banque -> facture mise a jour.
# Tout passe par le BFF, comme le ferait une autre ligne produit du groupe.
#
# Prerequis : la pile tourne (docker compose -f deploy/docker-compose.yml up --build).
#
#   pwsh scripts/e2e.ps1
#
# Le comportement depend de l'IBAN debiteur, pas d'un tirage aleatoire :
#   FR7630006000011234567890189 -> paid
#   FR7630004000031234567890143 -> paymentFailed (AM04, provision insuffisante)
#   FR1420041010050500013M02606 -> banque 503, la saga replanifie

$ErrorActionPreference = 'Stop'

$Keycloak = 'http://localhost:8080'
$Bff = 'http://localhost:5000'

function Get-Token {
    param([string]$ClientId, [string]$ClientSecret, [string]$Scope)

    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $ClientId
        client_secret = $ClientSecret
    }

    # Un scope optionnel n'est accorde que s'il est demande : c'est ce qui permet a un
    # client de ne porter, sur un appel donne, que les droits dont il a reellement besoin.
    if ($Scope) { $body['scope'] = $Scope }

    $response = Invoke-RestMethod -Method Post `
        -Uri "$Keycloak/realms/forterro/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body $body

    return $response.access_token
}

function Invoke-Scenario {
    param([string]$Label, [string]$Iban, [string]$Token, [int]$WaitSeconds = 20)

    Write-Host "`n=== $Label ($Iban)" -ForegroundColor Cyan

    $headers = @{
        Authorization     = "Bearer $Token"
        'Idempotency-Key' = [guid]::NewGuid().ToString()
    }

    $body = @{
        seller     = @{ name = 'Forterro France'; vatId = 'FR12345678901'; countryCode = 'FR' }
        buyer      = @{ name = 'Manufacture Dupont'; vatId = 'FR98765432109'; countryCode = 'FR' }
        currency   = 'EUR'
        debtorIban = $Iban
        dueDate    = '2026-12-31'
        lines      = @(@{ description = 'Licence ERP'; quantity = 2; unitPriceExclTax = 500; vatRate = 0.20 })
    } | ConvertTo-Json -Depth 5

    $invoice = Invoke-RestMethod -Method Post -Uri "$Bff/api/v1/invoices" `
        -Headers $headers -ContentType 'application/json' -Body $body
    Write-Host "  cree   : $($invoice.id) statut=$($invoice.status)"

    $issued = Invoke-RestMethod -Method Post -Uri "$Bff/api/v1/invoices/$($invoice.id)/issue" `
        -Headers @{ Authorization = "Bearer $Token" }
    Write-Host "  emise  : numero=$($issued.number) statut=$($issued.status)"

    # La chaine est asynchrone : on interroge jusqu'a sortie de l'etat 'issued'.
    # L'agregation du BFF renvoie facture et paiement en un seul appel.
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    do {
        Start-Sleep -Seconds 2
        $overview = Invoke-RestMethod -Uri "$Bff/bff/invoices/$($invoice.id)/overview" `
            -Headers @{ Authorization = "Bearer $Token" }
        Write-Host "  ...      $($overview.status)"
    } while ($overview.invoice.status -eq 'issued' -and (Get-Date) -lt $deadline)

    Write-Host "  final  : $($overview.invoice.status) — $($overview.status)" -ForegroundColor Yellow
    Write-Host "  paiement visible : $($overview.paymentAvailability)"
    return $overview
}

# erp-product-line porte invoicing:read et invoicing:write d'office, et demande en plus
# payments:read pour pouvoir afficher l'avancement du paiement. Elle n'a en revanche
# aucun moyen d'obtenir payments:write : elle ne peut pas initier de virement.
$token = Get-Token -ClientId 'erp-product-line' -ClientSecret 'erp-product-line-secret' -Scope 'payments:read'
Write-Host 'Jeton obtenu (erp-product-line + payments:read).' -ForegroundColor Green

Invoke-Scenario -Label 'Virement execute'       -Iban 'FR7630006000011234567890189' -Token $token | Out-Null
Invoke-Scenario -Label 'Provision insuffisante' -Iban 'FR7630004000031234567890143' -Token $token | Out-Null
Invoke-Scenario -Label 'Banque indisponible'    -Iban 'FR1420041010050500013M02606' -Token $token -WaitSeconds 12 | Out-Null

# Degradation : le meme appel, avec un jeton qui n'a PAS demande payments:read.
# La facture reste lisible, seul le bloc paiement disparait — l'agregation ne tombe pas.
Write-Host "`n=== Degradation sans le scope payments:read" -ForegroundColor Cyan
$limite = Get-Token -ClientId 'erp-product-line' -ClientSecret 'erp-product-line-secret'
$factures = Invoke-RestMethod -Uri "$Bff/api/v1/invoices?pageSize=1" -Headers @{ Authorization = "Bearer $limite" }
$cible = $factures.items[0].id
$degrade = Invoke-RestMethod -Uri "$Bff/bff/invoices/$cible/overview" -Headers @{ Authorization = "Bearer $limite" }
Write-Host "  facture lisible  : $($degrade.invoice.status)"
Write-Host "  paiement visible : $($degrade.paymentAvailability) — $($degrade.status)" -ForegroundColor Yellow

# Cloisonnement : un jeton payments-worker ne doit PAS pouvoir lire une facture.
$paymentsToken = Get-Token -ClientId 'payments-worker' -ClientSecret 'payments-worker-secret'
try {
    Invoke-RestMethod -Uri "$Bff/api/v1/invoices" -Headers @{ Authorization = "Bearer $paymentsToken" } | Out-Null
    Write-Host "`nKO : payments-worker a pu lire les factures." -ForegroundColor Red
}
catch {
    Write-Host "`nOK : payments-worker refuse sur les factures ($($_.Exception.Response.StatusCode))." -ForegroundColor Green
}

Write-Host "`nChemin navigateur  : ouvrez $Bff/bff/login (demo / demo), puis $Bff/bff/me"
Write-Host "Traces distribuees : http://localhost:16686 — un seul traceId du BFF jusqu'a la banque."
