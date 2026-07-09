// ManageComingSoon - Library Path Picker (nested list, SDK ChildCollections pattern)

namespace ManageComingSoon.UI.AddMovie
{
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using MediaBrowser.Model.GenericEdit;

    // Alias eliminates ambiguity with System.ComponentModel.IEditableObject
    using MBEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;

    // -----------------------------------------------------------------------
    // Single path entry inside a library
    // -----------------------------------------------------------------------
    public class LibraryPathItem : EditableOptionsBase
    {
        public override string EditorTitle => string.Empty;

        [DisplayName("Path")]
        public string Path { get; set; } = string.Empty;

        [DisplayName("Preferred")]
        public bool IsPreferred { get; set; } = false;
    }

    // -----------------------------------------------------------------------
    // Collection of paths for one library
    // -----------------------------------------------------------------------
    public class LibraryPathCollection :
        List<LibraryPathItem>,
        IEditableObjectCollection,
        IEnumerable<LibraryPathItem>
    {
        IEnumerator<MBEditableObject> IEnumerable<MBEditableObject>.GetEnumerator()
            => (IEnumerator<MBEditableObject>)this.GetEnumerator();

        IEnumerator<LibraryPathItem> IEnumerable<LibraryPathItem>.GetEnumerator()
            => (IEnumerator<LibraryPathItem>)this.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }

    // -----------------------------------------------------------------------
    // A library entry (name + nested list of paths)
    // -----------------------------------------------------------------------
    public class LibraryEntry : EditableOptionsBase
    {
        public override string EditorTitle => string.Empty;

        [DisplayName("Library Name")]
        public string LibraryName { get; set; } = string.Empty;

        [DisplayName("Paths")]
        public LibraryPathCollection Paths { get; set; } = new LibraryPathCollection();
    }

    // -----------------------------------------------------------------------
    // Top-level collection of library entries
    // -----------------------------------------------------------------------
    public class LibraryEntryCollection :
        List<LibraryEntry>,
        IEditableObjectCollection,
        IEnumerable<LibraryEntry>
    {
        IEnumerator<MBEditableObject> IEnumerable<MBEditableObject>.GetEnumerator()
            => (IEnumerator<MBEditableObject>)this.GetEnumerator();

        IEnumerator<LibraryEntry> IEnumerable<LibraryEntry>.GetEnumerator()
            => (IEnumerator<LibraryEntry>)this.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
