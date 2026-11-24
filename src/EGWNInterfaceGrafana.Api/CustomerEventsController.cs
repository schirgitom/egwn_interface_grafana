using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace EGWNInterfaceGrafana.Api;

[ApiController]
[Route("internal/customers")]
public sealed class CustomerEventsController(EventProcessor processor) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Post([FromBody] CustomerSnapshot customer, CancellationToken ct)
    {
        try
        {
            var eventId = await processor.ProcessSnapshotAsync(customer, ct);
            return Ok(new { eventId });
        }
        catch (InvalidEventException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
