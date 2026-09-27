namespace CrptWorkbench.Domain;

// True API v733.0 section 4.1.2.4.
public static class PackageSetRules
{
    public static bool SupportsGroup(string group) => group is
        "antiseptic" or "grocery" or "nabeer" or "bio" or "perfumery" or "toys" or
        "conserve" or "petfood" or "wheelchairs" or "milk" or "seafood" or "autofluids" or
        "chemistry" or "fire" or "books" or "lp" or "radio" or "vegetableoil" or
        "sweets" or "softdrinks" or "electronics";
}
