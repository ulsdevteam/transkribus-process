using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

class Database : DbContext
{
    public DbSet<Page> Pages { get; set; }
    private IConfiguration Config { get; }

    public Database(IConfiguration config)
    {
        Config = config;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var connectionString = Config["CONNECTION_STRING"];
        if (connectionString.StartsWith("Filename=")) 
        { 
            options.UseSqlite(connectionString); 
        }
        else
        {
            options.UseMySQL(connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Page>().HasKey(p => p.ProcessId);
        model.Entity<Page>().HasIndex(p => p.InProgress);
    }
}

class Page
{
    public string Pid { get; set; }
    public int HtrId { get; set; }
    public int ProcessId { get; set;}
    public bool InProgress { get; set; }
    public string User { get; set; }
    public DateTime Uploaded { get; set; }
    public DateTime? Downloaded { get; set; }
}