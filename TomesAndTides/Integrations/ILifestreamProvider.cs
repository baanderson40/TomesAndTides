namespace TomesAndTides.Integrations;

public interface ILifestreamProvider
{
    bool TryGetBusy(out bool busy);
    bool TryChangeCharacter(string name, string world, out string result);
}
