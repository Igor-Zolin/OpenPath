var password = Environment.GetEnvironmentVariable("SSH_ENV_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    return 1;
}

Console.Write(password);
return 0;
