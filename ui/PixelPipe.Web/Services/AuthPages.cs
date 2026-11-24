using System.Net;
using System.Text;

namespace PixelPipe.Web.Services;

public static class AuthPages
{
    public static string Form(string title, string action, string token, string fields, string? error)
    {
        var message = string.IsNullOrEmpty(error)
            ? ""
            : $"<p class=\"err\">{WebUtility.HtmlEncode(error)}</p>";
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{WebUtility.HtmlEncode(title)}} · PixelPipe</title>
              <link rel="stylesheet" href="app.css" />
              <style>
                body { margin: 0; background: #0E0E12; color: #F4F4F8; font-family: sans-serif; }
                main { max-width: 28rem; margin: 8vh auto; padding: 1.5rem; background: #17171F; border-radius: 12px; }
                label { display: block; margin: 0.8rem 0 0.2rem; }
                input, select, button { width: 100%; box-sizing: border-box; padding: 0.6rem; }
                button { margin-top: 1rem; background: #7C5CFF; color: white; border: 0; border-radius: 8px; }
                .err { color: #ffb4a8; }
                a { color: #FFB703; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                {{message}}
                <form method="post" action="{{action}}">
                  <input type="hidden" name="__RequestVerificationToken" value="{{WebUtility.HtmlEncode(token)}}" />
                  {{fields}}
                  <button type="submit">Continue</button>
                </form>
              </main>
            </body>
            </html>
            """;
    }

    public static string Login(string token, string? error) => Form(
        "Sign in",
        "/login",
        token,
        """
        <label>Username <input name="username" autocomplete="username" required /></label>
        <label>Password <input id="pw" name="password" type="password" autocomplete="current-password" required /></label>
        <label><input type="checkbox" style="width:auto" onclick="document.getElementById('pw').type=this.checked?'text':'password'" /> Show password</label>
        <label><input type="checkbox" name="remember" value="1" style="width:auto" /> Remember me</label>
        """,
        error);

    public static string Setup(string token, string? error) => Form(
        "Create the admin account",
        "/setup",
        token,
        """
        <p>This is the first time PixelPipe has been opened. The admin can add players and change settings.</p>
        <label>Admin username <input name="username" autocomplete="username" required /></label>
        <label>Password <input id="pw" name="password" type="password" autocomplete="new-password" required /></label>
        <label>Confirm password <input id="pw2" name="confirm" type="password" autocomplete="new-password" required /></label>
        <label><input type="checkbox" style="width:auto" onclick="for (const id of ['pw','pw2']) document.getElementById(id).type=this.checked?'text':'password'" /> Show password</label>
        """,
        error);

    public static string Logout(string token) => Form(
        "Sign out",
        "/logout",
        token,
        "<p>Sign out of PixelPipe on this browser?</p>",
        null);
}
