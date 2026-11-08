using Microsoft.Extensions.Logging;
using SmartDock.Core.Interfaces;
using SmartDock.Infrastructure.Configuration;

namespace SmartDock.Infrastructure.Persistence;

/// <summary>
/// Notes kept in %LOCALAPPDATA%\SmartDock\notes.json. A JSON file (atomic write, corrupt-file recovery)
/// is sufficient for a personal notes list; SQLite remains the plan for larger histories.
/// </summary>
public sealed class JsonNotesStore : INotesStore
{
    private readonly JsonFileStore<List<Note>> _store;
    private readonly Lock _gate = new();
    private List<Note> _notes;

    public JsonNotesStore(AppPaths paths, ILogger<JsonNotesStore> logger)
    {
        _store = new JsonFileStore<List<Note>>(Path.Combine(paths.Root, "notes.json"), () => [], logger);
        _notes = _store.Load().Value.Where(n => n is not null).ToList();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Note> GetAll()
    {
        lock (_gate)
        {
            return _notes.OrderByDescending(n => n.UpdatedAt).ToList();
        }
    }

    public Note Save(Note note)
    {
        lock (_gate)
        {
            note.UpdatedAt = DateTimeOffset.Now;
            var i = _notes.FindIndex(n => n.Id == note.Id);
            if (i >= 0)
            {
                _notes[i] = note;
            }
            else
            {
                _notes.Add(note);
            }

            _store.Save(_notes);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return note;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _notes.RemoveAll(n => n.Id == id) > 0;
            if (removed)
            {
                _store.Save(_notes);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }
}
