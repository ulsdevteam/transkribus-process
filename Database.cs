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