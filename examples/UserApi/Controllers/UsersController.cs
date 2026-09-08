using Microsoft.AspNetCore.Mvc;
using UserApi.Contracts;
using UserApi.Services;

namespace UserApi.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserService service)
    : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserResponse>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var user = await service.GetAsync(id, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> GetActive(
        CancellationToken cancellationToken)
    {
        var users = await service.GetActiveAsync(cancellationToken);
        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await service.CreateAsync(request, cancellationToken);

        return CreatedAtAction(
            nameof(Get),
            new { id = user.Id },
            user);
    }
}
