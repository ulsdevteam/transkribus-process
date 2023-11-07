using Microsoft.EntityFrameworkCore;

class Database : DbContext
{
    public DbSet<Page> Pages { get; set; }
    public string DbPath { get; }

    public Database(string dbPath)
    {
        DbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Page>().HasKey(p => p.Pid);
    }
}

class Page
{
    public string Pid { get; set; }
    public int ProcessId { get; set;}
    public bool InProgress { get; set; }
}