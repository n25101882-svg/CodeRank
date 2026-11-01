using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodeRank.Api.Data;

public interface IProblemDeletionConflictDetector
{
    bool IsConstraintViolation(DbUpdateException error);
}

public sealed class SqlServerProblemDeletionConflictDetector : IProblemDeletionConflictDetector
{
    // 547 also covers CHECK failures; the caller must confirm an OlympiadProblems link.
    public bool IsConstraintViolation(DbUpdateException error) =>
        error.InnerException is SqlException { Number: 547 };
}
