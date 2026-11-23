namespace JobDist.Core.Services;

/// <summary>Base class of expected business errors (their message is safe to show to clients).</summary>
public abstract class JobDistException(string message) : Exception(message);

/// <summary>Entity not found (HTTP 404).</summary>
public sealed class NotFoundException(string message) : JobDistException(message);

/// <summary>Input failed business validation (HTTP 400).</summary>
public sealed class ValidationException(string message) : JobDistException(message);

/// <summary>Operation conflicts with the current state (HTTP 409).</summary>
public sealed class ConflictException(string message) : JobDistException(message);

/// <summary>Caller is authenticated but not allowed to act on this resource (HTTP 403).</summary>
public sealed class ForbiddenException(string message) : JobDistException(message);
