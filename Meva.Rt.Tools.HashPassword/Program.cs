using System.Security.Cryptography;
using System.Text;

Console.Write("Ingrese contraseña: ");

var password = new StringBuilder();
while (true)
{
    var key = Console.ReadKey(intercept: true);
    if (key.Key == ConsoleKey.Enter) break;
    if (key.Key == ConsoleKey.Backspace && password.Length > 0)
    {
        password.Remove(password.Length - 1, 1);
        Console.Write("\b \b");
    }
    else if (key.Key != ConsoleKey.Backspace)
    {
        password.Append(key.KeyChar);
        Console.Write("*");
    }
}
Console.WriteLine();

var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password.ToString())));
Console.WriteLine($"Hash SHA-256: {hash}");
