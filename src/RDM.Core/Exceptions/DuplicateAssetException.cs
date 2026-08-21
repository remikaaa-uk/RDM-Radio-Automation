namespace RDM.Core;

public sealed class DuplicateAssetException : Exception
{
    public DuplicateAssetException()
        : base("An asset with the same checksum already exists in the library.") { }
}
