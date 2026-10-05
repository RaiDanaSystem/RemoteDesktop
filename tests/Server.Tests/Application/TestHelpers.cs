using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Infrastructure.Persistence;

namespace RemoteSupport.Server.Tests.Application;

public static class TestHelpers
{
    public static ApplicationDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
