using FluentValidation;
using Forterro.BuildingBlocks.Api;
using Forterro.Invoicing.Api.Application;
using Forterro.Invoicing.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Forterro.Invoicing.Api.Endpoints;

public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/invoices")
            .WithTags("Invoices")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateDraftAsync)
            .WithName("CreateInvoiceDraft")
            .WithSummary("Cree une facture en brouillon.")
            .AddEndpointFilter<IdempotencyFilter>()
            .RequireAuthorization(Policies.InvoicingWrite)
            .Produces<InvoiceResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapPost("/{id:guid}/issue", IssueAsync)
            .WithName("IssueInvoice")
            .WithSummary("Emet la facture : numerotation legale et publication de InvoiceIssued.")
            .AddEndpointFilter<IdempotencyFilter>()
            .RequireAuthorization(Policies.InvoicingWrite)
            .Produces<InvoiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("CancelInvoice")
            .WithSummary("Annule une facture non payee.")
            .RequireAuthorization(Policies.InvoicingWrite)
            .Produces<InvoiceResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetInvoice")
            .WithSummary("Recupere une facture par identifiant.")
            .RequireAuthorization(Policies.InvoicingRead)
            .Produces<InvoiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", ListAsync)
            .WithName("ListInvoices")
            .WithSummary("Liste paginee (curseur seek) des factures.")
            .RequireAuthorization(Policies.InvoicingRead)
            .Produces<PagedResult<InvoiceResponse>>();

        return app;
    }

    private static async Task<IResult> CreateDraftAsync(
        CreateInvoiceRequest request,
        IValidator<CreateInvoiceRequest> validator,
        IInvoiceService service,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        var invoice = await service.CreateDraftAsync(request, cancellationToken);

        return Results.Created($"/api/v1/invoices/{invoice.Id}", invoice);
    }

    private static async Task<IResult> IssueAsync(
        Guid id,
        IInvoiceService service,
        CancellationToken cancellationToken)
        => Results.Ok(await service.IssueAsync(id, cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid id,
        CancelInvoiceRequest request,
        IValidator<CancelInvoiceRequest> validator,
        IInvoiceService service,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        return Results.Ok(await service.CancelAsync(id, request.Reason, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IInvoiceService service,
        CancellationToken cancellationToken)
        => Results.Ok(await service.GetAsync(id, cancellationToken));

    private static async Task<IResult> ListAsync(
        IInvoiceService service,
        CancellationToken cancellationToken,
        [FromQuery] InvoiceStatus? status = null,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? cursor = null)
        => Results.Ok(await service.ListAsync(status, pageSize, cursor, cancellationToken));
}

public static class Policies
{
    public const string InvoicingRead = "invoicing:read";
    public const string InvoicingWrite = "invoicing:write";
}
