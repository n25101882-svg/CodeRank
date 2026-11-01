using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Authentication;

public interface IRegistrationConflictDetector
{
    bool IsUniqueViolation(DbUpdateException error);
}

public sealed class SqlServerRegistrationConflictDetector : IRegistrationConflictDetector
{
    public bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is SqlException { Number: 2601 or 2627 };
}
