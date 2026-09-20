using System.Text;

namespace ClypDat.App.Services;

internal enum BrowserCallbackService
{
    ClypDat,
    Spotify,
    Xbox,
}

/// <summary>
/// The page the browser lands on when a sign-in hands back to the app's
/// loopback listener. It is the last thing the user sees of the flow, and they
/// arrive at it straight from clypdat.xyz - so it wears the website's look
/// (colours, type, card and wash) rather than a one-off style of its own.
/// Everything is inline: the listener answers one request and stops, so there
/// is nothing to serve a stylesheet or image from afterwards. The two web fonts
/// are the only fetch, and the fallback stack stands in if they do not load.
/// </summary>
internal static class BrowserCallbackPage
{
    private const string LogoResource = "ClypDat.App.Assets.clypdat-logo.svg";

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="referrer" content="no-referrer">
          <title>__NAME__ connected</title>
          <link rel="preconnect" href="https://fonts.googleapis.com">
          <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
          <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,600&amp;family=Geist:wght@400;500;600&amp;display=swap">
          <style>
            :root {
              color-scheme: dark;
              --background: #0a0d11;
              --foreground: #e9eef4;
              --display: #f4f7fa;
              --muted: #a1a1aa;
              --faint: #71717a;
              --accent: #6ee7b7;
              --ease: cubic-bezier(0.16, 1, 0.3, 1);
            }
            *, *::before, *::after { box-sizing: border-box; }
            html { background: var(--background); }
            body {
              margin: 0;
              min-height: 100vh;
              display: grid;
              place-items: center;
              padding: 48px 20px;
              color: var(--foreground);
              font: 15px/1.6 Geist, ui-sans-serif, system-ui, "Segoe UI", sans-serif;
              -webkit-font-smoothing: antialiased;
            }

            .ambience { position: fixed; inset: 0; z-index: -1; overflow: hidden; pointer-events: none; }
            .ambience span { position: absolute; }
            /* The drifting washes are drawn by the shader in the script, onto
               .field. These CSS gradients are only the fallback for when WebGL
               is unavailable: a gradient this faint gets ~20 shades to span
               hundreds of pixels, so as CSS it shows rings. The shader mixes in
               full precision and dithers the final colour, which CSS cannot.
               Same positions, sizes, drift and smoothstep falloff either way. */
            .field { position: absolute; inset: 0; width: 100%; height: 100%; display: none; }
            .ambience.live .field { display: block; }
            .ambience.live .wash { display: none; }
            .wash { will-change: transform; background: radial-gradient(closest-side, rgb(var(--rgb) / var(--peak)), rgb(var(--rgb) / calc(var(--peak) * 0.84)) 25%, rgb(var(--rgb) / calc(var(--peak) * 0.5)) 50%, rgb(var(--rgb) / calc(var(--peak) * 0.16)) 75%, rgb(var(--rgb) / 0)); }
            .wash-a { --rgb: 16 185 129; --peak: 0.13; left: -14%; top: -22%; width: 1220px; height: 1040px; animation: drift-a 19s ease-in-out infinite; }
            .wash-b { --rgb: 45 212 191; --peak: 0.09; right: -20%; top: 18%; width: 1160px; height: 980px; animation: drift-b 23s ease-in-out infinite; }
            .wash-c { --rgb: 6 182 212; --peak: 0.08; bottom: -18%; left: 14%; width: 1200px; height: 940px; animation: drift-c 31s ease-in-out infinite; }
            .grain {
              inset: 0;
              opacity: 0.025;
              background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='140' height='140'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.85' numOctaves='3' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='140' height='140' filter='url(%23n)' opacity='0.5'/%3E%3C/svg%3E");
            }

            .stack { width: min(100%, 420px); display: flex; flex-direction: column; align-items: center; gap: 20px; }
            .stack > * { animation: rise 0.8s var(--ease) both; }
            .stack > .card { animation-delay: 0.08s; }

            .brand {
              display: inline-flex;
              align-items: center;
              gap: 10px;
              height: 44px;
              padding: 0 18px 0 16px;
              border: 1px solid rgb(255 255 255 / 0.09);
              border-radius: 16px;
              background: rgb(12 16 21 / 0.55);
              backdrop-filter: blur(12px);
              color: #fafafa;
              font-size: 17px;
              font-weight: 600;
              letter-spacing: -0.02em;
            }
            .brand svg { width: 32px; height: 32px; }

            .card {
              position: relative;
              width: 100%;
              padding: 36px 28px 28px;
              overflow: hidden;
              text-align: center;
              border: 1px solid rgb(255 255 255 / 0.1);
              border-radius: 24px;
              background: rgb(255 255 255 / 0.04);
              box-shadow: 0 25px 50px -12px rgb(0 0 0 / 0.3);
            }
            .card::before {
              content: "";
              position: absolute;
              inset: 0;
              pointer-events: none;
              background: radial-gradient(70% 55% at 50% 0%, rgb(52 211 153 / 0.12), transparent);
            }
            .card > * { position: relative; }

            .badge {
              width: 56px;
              height: 56px;
              margin: 4px auto 26px;
              display: grid;
              place-items: center;
              border: 1px solid rgb(110 231 183 / 0.25);
              border-radius: 18px;
              background: rgb(110 231 183 / 0.12);
              box-shadow: 0 0 40px -8px rgb(52 211 153 / 0.45);
            }
            /* The ripple is an outline on a layer the exact shape of the badge,
               pushed outward by its offset. The browser grows the corner radius
               with the offset, so the ring stays concentric and a crisp 1px the
               whole way out. Scaling the layer instead stretched the radius and
               the hairline with it, leaving the corners tighter than the sides. */
            .badge::after {
              content: "";
              position: absolute;
              inset: -1px;
              place-self: stretch;
              border-radius: inherit;
              outline: 1px solid rgb(110 231 183 / 0.45);
              outline-offset: 0;
              opacity: 0;
              animation: ripple 2.6s ease-out 0.9s infinite;
            }
            /* The tick's path sits a unit above centre on purpose: its short arm
               hangs low, so a box-centred tick reads as sitting low. */
            .badge svg { width: 26px; height: 26px; fill: none; stroke: var(--accent); stroke-width: 2.5; stroke-linecap: round; stroke-linejoin: round; }
            .badge path { stroke-dasharray: 1; stroke-dashoffset: 1; animation: draw 0.6s var(--ease) 0.35s forwards; }

            .eyebrow { margin: 0; color: var(--accent); font-size: 12px; font-weight: 500; letter-spacing: 0.2em; text-transform: uppercase; }
            h1 {
              margin: 10px 0 0;
              color: var(--display);
              font-family: "Bricolage Grotesque", Geist, ui-sans-serif, system-ui, "Segoe UI", sans-serif;
              font-size: 30px;
              font-weight: 600;
              line-height: 1.15;
              letter-spacing: -0.025em;
            }
            .accent {
              background-image: linear-gradient(100deg, #6ee7b7, #34d399 55%, #2dd4bf);
              -webkit-background-clip: text;
              background-clip: text;
              color: transparent;
            }
            .detail { margin: 10px 0 0; color: var(--muted); font-size: 14px; line-height: 1.7; }

            .button {
              margin-top: 26px;
              display: flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              width: 100%;
              padding: 12px 16px;
              border-radius: 999px;
              background: var(--accent);
              box-shadow: 0 10px 30px -10px rgb(52 211 153 / 0.55);
              color: #022c22;
              font-size: 14px;
              font-weight: 600;
              text-decoration: none;
              transition: background-color 0.2s;
            }
            .button:hover { background: #a7f3d0; }
            .button span { transition: transform 0.2s var(--ease); }
            .button:hover span { transform: translateX(3px); }
            .button:focus-visible { outline: 2px solid #34d399; outline-offset: 3px; }
            .hint { margin: 16px 0 0; color: var(--faint); font-size: 13px; }

            @keyframes drift-a { 50% { transform: translate3d(8%, 6%, 0); } }
            @keyframes drift-b { 50% { transform: translate3d(-10%, 7%, 0); } }
            @keyframes drift-c { 50% { transform: translate3d(7%, -8%, 0); } }
            @keyframes rise { from { opacity: 0; transform: translate3d(0, 14px, 0); } }
            @keyframes draw { to { stroke-dashoffset: 0; } }
            @keyframes ripple {
              0% { opacity: 1; outline-offset: 0; }
              70%, 100% { opacity: 0; outline-offset: 14px; }
            }

            @media (prefers-reduced-motion: reduce) {
              .stack > *, .badge::after, .ambience span { animation: none; }
              .badge path { animation: none; stroke-dashoffset: 0; }
              .button span { transition: none; }
            }
          </style>
        </head>
        <body>
          <div class="ambience" aria-hidden="true">
            <canvas class="field"></canvas>
            <span class="wash wash-a"></span>
            <span class="wash wash-b"></span>
            <span class="wash wash-c"></span>
            <span class="grain"></span>
          </div>
          <main class="stack">
            <div class="brand">__LOGO__<span>ClypDat</span></div>
            <section class="card" role="status">
              <div class="badge" aria-hidden="true">
                <svg viewBox="0 0 24 24"><path pathLength="1" d="M5 11.5l4.5 4.5L19 6.5" /></svg>
              </div>
              <p class="eyebrow">Account linked</p>
              <h1>__NAME__ is <span class="accent">connected</span></h1>
              <p class="detail">__DETAIL__</p>
              <a class="button" href="https://www.clypdat.xyz/account">Manage your account <span aria-hidden="true">&rarr;</span></a>
              <p class="hint">You can close this tab and go back to ClypDat.</p>
            </section>
          </main>
          <script>
            // The callback URL carries the one-time token and state. The app has
            // already taken them, so drop them from the address bar and history.
            history.replaceState(null, "", location.pathname);

            // The washes, drawn on the GPU. Base colour and all three washes are
            // mixed per pixel in full precision, then the final colour gets
            // +-1 step of fresh noise before the display rounds it to 8 bits.
            // That dither is what removes the rings: the step between shades
            // dissolves into grain too fine to see. It has to happen on the
            // final colour - dithering each translucent wash separately was
            // undone by the browser storing it premultiplied, and by the
            // sub-pixel sampling as it drifted.
            //
            // Positions, sizes and drift match the CSS fallback above. Capped at
            // 30fps - the drift is slow enough that more is wasted work - and
            // rAF stops by itself while the tab is hidden.
            (() => {
              const ambience = document.querySelector(".ambience");
              const canvas = ambience.querySelector(".field");
              const gl = canvas.getContext("webgl", { alpha: false, antialias: false, depth: false, stencil: false, powerPreference: "low-power" });
              if (!gl) return;

              const vertexSource = "attribute vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }";
              const fragmentSource = `
                precision highp float;
                uniform vec2 size;   // viewport, CSS px
                uniform float scale; // device px per CSS px
                uniform float time;  // seconds
                uniform float frame; // changes the noise each frame

                vec3 wash(vec3 base, vec2 point, vec2 center, vec2 radii, vec3 rgb, float peak) {
                  float f = clamp(1.0 - length((point - center) / radii), 0.0, 1.0);
                  return mix(base, rgb / 255.0, peak * f * f * (3.0 - 2.0 * f));
                }

                // 0 -> 1 -> 0 over one period, eased at both ends.
                float drift(float period) {
                  return 0.5 - 0.5 * cos(6.2831853 * time / period);
                }

                // Sine-free hash (Hoskins), so it holds up at large coordinates.
                float hash(vec2 p) {
                  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
                  p3 += dot(p3, p3.yzx + 33.33);
                  return fract((p3.x + p3.y) * p3.z);
                }

                void main() {
                  vec2 point = vec2(gl_FragCoord.x, size.y * scale - gl_FragCoord.y) / scale;
                  vec3 colour = vec3(10.0, 13.0, 17.0) / 255.0;
                  colour = wash(colour, point,
                    vec2(-0.14 * size.x + 610.0, -0.22 * size.y + 520.0) + vec2(97.6, 62.4) * drift(19.0),
                    vec2(610.0, 520.0), vec3(16.0, 185.0, 129.0), 0.13);
                  colour = wash(colour, point,
                    vec2(1.2 * size.x - 580.0, 0.18 * size.y + 490.0) + vec2(-116.0, 68.6) * drift(23.0),
                    vec2(580.0, 490.0), vec3(45.0, 212.0, 191.0), 0.09);
                  colour = wash(colour, point,
                    vec2(0.14 * size.x + 600.0, 1.18 * size.y - 470.0) + vec2(84.0, -75.2) * drift(31.0),
                    vec2(600.0, 470.0), vec3(6.0, 182.0, 212.0), 0.08);
                  vec2 seed = gl_FragCoord.xy + frame * vec2(7.31, 3.17);
                  float noise = hash(seed) + hash(seed + 91.7) - 1.0;
                  gl_FragColor = vec4(colour + noise / 255.0, 1.0);
                }`;

              const compile = (type, source) => {
                const shader = gl.createShader(type);
                gl.shaderSource(shader, source);
                gl.compileShader(shader);
                return gl.getShaderParameter(shader, gl.COMPILE_STATUS) ? shader : null;
              };
              const vertex = compile(gl.VERTEX_SHADER, vertexSource);
              const fragment = compile(gl.FRAGMENT_SHADER, fragmentSource);
              if (!vertex || !fragment) return;
              const program = gl.createProgram();
              gl.attachShader(program, vertex);
              gl.attachShader(program, fragment);
              gl.linkProgram(program);
              if (!gl.getProgramParameter(program, gl.LINK_STATUS)) return;
              gl.useProgram(program);

              gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
              gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
              const position = gl.getAttribLocation(program, "p");
              gl.enableVertexAttribArray(position);
              gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
              const uniform = (name) => gl.getUniformLocation(program, name);
              const sizeUniform = uniform("size");
              const scaleUniform = uniform("scale");
              const timeUniform = uniform("time");
              const frameUniform = uniform("frame");

              const still = matchMedia("(prefers-reduced-motion: reduce)");
              const started = performance.now();
              let frame = 0;
              let last = -Infinity;
              let queued = false;

              const draw = (now) => {
                const width = window.innerWidth;
                const height = window.innerHeight;
                const scale = Math.min(window.devicePixelRatio || 1, 2);
                const pixelWidth = Math.round(width * scale);
                const pixelHeight = Math.round(height * scale);
                if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
                  canvas.width = pixelWidth;
                  canvas.height = pixelHeight;
                  gl.viewport(0, 0, pixelWidth, pixelHeight);
                }
                gl.uniform2f(sizeUniform, width, height);
                gl.uniform1f(scaleUniform, pixelWidth / width);
                gl.uniform1f(timeUniform, still.matches ? 0 : (now - started) / 1000);
                gl.uniform1f(frameUniform, frame = (frame + 1) % 1024);
                gl.drawArrays(gl.TRIANGLES, 0, 3);
              };
              const tick = (now) => {
                queued = false;
                if (now - last >= 32) {
                  last = now;
                  draw(now);
                }
                if (!still.matches) schedule();
              };
              const schedule = () => {
                if (queued) return;
                queued = true;
                requestAnimationFrame(tick);
              };

              draw(performance.now());
              ambience.classList.add("live");
              schedule();
              // Held still, the field only needs redrawing when it changes size.
              window.addEventListener("resize", () => { last = -Infinity; schedule(); });
              still.addEventListener("change", schedule);
            })();
          </script>
        </body>
        </html>
        """;

    private static readonly Lazy<string> Shell = new(() => Template.Replace("__LOGO__", LoadLogo(), StringComparison.Ordinal));

    public static byte[] Success(BrowserCallbackService service)
    {
        var (name, detail) = service switch
        {
            BrowserCallbackService.Spotify => ("Spotify", "Your Spotify account is linked to the ClypDat desktop app."),
            BrowserCallbackService.Xbox => ("Xbox", "Desktop Capture clips can now be named after the game you are playing on Xbox."),
            _ => ("ClypDat", "Your account is linked to the ClypDat desktop app."),
        };
        var html = Shell.Value
            .Replace("__NAME__", name, StringComparison.Ordinal)
            .Replace("__DETAIL__", detail, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(html);
    }

    // The mark is decorative - the wordmark beside it names the brand - so a
    // missing resource costs only the icon, never the page.
    private static string LoadLogo()
    {
        using var stream = typeof(BrowserCallbackPage).Assembly.GetManifestResourceStream(LogoResource);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim().Replace("<svg ", "<svg aria-hidden=\"true\" ", StringComparison.Ordinal);
    }
}
