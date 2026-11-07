using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Data;

public interface IProblemDeletionConflictDetector
{
    bool IsConstraintViolation(DbUpdateException error);
}

public sealed class SqlServerProblemDeletionConflictDetector : IProblemDeletionConflictDetector
{
    // 547 also covers CHECK failures; the caller must confirm a blocking dependency.
    public bool IsConstraintViolation(DbUpdateException error) =>
        error.InnerException is SqlException { Number: 547 };
}
