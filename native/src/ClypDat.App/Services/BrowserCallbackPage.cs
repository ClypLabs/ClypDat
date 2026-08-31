using System.Text;

namespace ClypDat.App.Services;

internal static class BrowserCallbackPage
{
    private const string SuccessHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ClypDat connected</title>
          <style>
            :root { color-scheme: dark; font-family: Inter, ui-sans-serif, system-ui, sans-serif; }
            * { box-sizing: border-box; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 24px; color: #f4f7f6; background: #101315; }
            main { width: min(100%, 440px); padding: 42px 38px 36px; text-align: center; border: 1px solid #293936; border-radius: 24px; background: linear-gradient(145deg, #19201f, #111615); box-shadow: 0 24px 70px #0008; }
            .mark { width: 64px; height: 64px; margin: 0 auto 24px; display: grid; place-items: center; border-radius: 20px; color: #07231b; background: #8cf2c3; box-shadow: 0 0 0 8px #8cf2c31a, 0 10px 28px #8cf2c33d; font-size: 34px; font-weight: 800; }
            h1 { margin: 0; font-size: 27px; letter-spacing: -.04em; }
            p { margin: 12px 0 0; color: #a9b8b3; font-size: 15px; line-height: 1.55; }
            .hint { margin-top: 26px; color: #6f817b; font-size: 13px; }
          </style>
        </head>
        <body>
          <main>
            <div class="mark" aria-hidden="true">✓</div>
            <h1>ClypDat connected!</h1>
            <p>Your account is linked to the ClypDat desktop app.</p>
            <p class="hint">You may close this tab and return to ClypDat.</p>
          </main>
        </body>
        </html>
        """;

    public static byte[] Success() => Encoding.UTF8.GetBytes(SuccessHtml);
}
