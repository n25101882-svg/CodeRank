using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CodeRank.Judge.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CodeRank.Api.Judge;

public sealed class JudgeWorkerAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    JudgeWorkerSettings workers) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var workerIds = Request.Headers[WorkerCredentials.WorkerIdHeader];
        var authorization = Request.Headers.Authorization;
        if (!Request.IsHttps || workerIds.Count != 1 || !WorkerCredentials.IsValidId(workerIds[0]) ||
            authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !string.Equals(header.Scheme, WorkerCredentials.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !workers.Authenticate(workerIds[0]!, header.Parameter))
            return Task.FromResult(AuthenticateResult.Fail("Judge worker authentication failed."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, workerIds[0]!)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
