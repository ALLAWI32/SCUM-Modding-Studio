namespace ScumStudio.Rendering.Gl;

/// <summary>GLSL 4.30 core sources of the built-in programs.</summary>
/// <remarks>
/// Matrices are uploaded untransposed from row-vector <see cref="System.Numerics.Matrix4x4"/> values, which GLSL reads
/// as the equivalent column-vector matrices (<c>gl_Position = uViewProj * iModel * vec4(p, 1)</c>).
/// Shading is done in linear space; colour targets are plain RGBA8 and the fragment shaders encode sRGB themselves
/// (<c>uEncodeSrgb</c>) so read-back pixels are display-ready regardless of driver sRGB read/blit behaviour.
/// </remarks>
internal static class ShaderSources
{
    /// <summary>Shared vertex shader of the mesh colour and ID passes (instanced attributes, locations 3..8).</summary>
    public const string MeshVertex = """
        #version 430 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in mat4 iModel;
        layout(location = 7) in vec4 iTint;
        layout(location = 8) in uvec2 iIdFlags;
        layout(location = 9) in vec2 iSurface;

        uniform mat4 uViewProj;
        uniform int uBillboard;
        uniform vec3 uCameraRight;
        uniform vec3 uCameraUp;
        uniform vec3 uCameraPosition;

        out vec3 vNormal;
        out vec3 vView;
        out vec2 vUv;
        out vec3 vWorld;
        out vec4 vTint;
        flat out uint vPickId;
        flat out uint vFlags;
        flat out vec2 vSurface;

        void main()
        {
            vec4 world = iModel * vec4(aPosition, 1.0);
            mat3 m = mat3(iModel);
            float det = determinant(m);
            mat3 normalMatrix = abs(det) > 1e-12 ? transpose(inverse(m)) : m;
            vNormal = normalMatrix * aNormal;
            if (uBillboard != 0)
            {
                // A camera-facing card (an item's inventory icon): the mesh's x/y span the camera's right and up at the
                // instance's origin, scaled like the instance; it faces the camera, so it is lit as if seen head-on.
                vec3 origin = (iModel * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
                world = vec4(origin + uCameraRight * (aPosition.x * length(iModel[0].xyz)) + uCameraUp * (aPosition.y * length(iModel[1].xyz)), 1.0);
                vNormal = cross(uCameraRight, uCameraUp);
            }

            vUv = aUv;
            vWorld = world.xyz;
            vView = world.xyz - uCameraPosition; // small numbers: smooth screen derivatives for normal mapping near the camera
            vTint = iTint;
            vPickId = iIdFlags.x;
            vFlags = iIdFlags.y;
            vSurface = iSurface;
            gl_Position = uViewProj * world;
        }
        """;

    /// <summary>
    /// Colour pass: (texture * tint) with a hemispheric ambient + one directional key light, optional exponential distance
    /// fog, selection tint + rim. Output alpha = albedo alpha (blended only in the translucent pass). Shiny paint
    /// (instance surface: metal, gloss) adds a studio reflection, a key-light highlight and a clear coat.
    /// </summary>
    public const string MeshFragment = """
        #version 430 core
        in vec3 vNormal;
        in vec2 vUv;
        in vec3 vWorld;
        in vec3 vView;
        in vec4 vTint;
        flat in uint vPickId;
        flat in uint vFlags;
        flat in vec2 vSurface;

        uniform sampler2D uTexture;
        uniform sampler2D uNormalMap;
        uniform int uHasNormalMap;
        uniform vec2 uRoughness;
        uniform int uHasTexture;
        uniform float uAlphaCutoff;
        uniform vec4 uSectionTint;
        uniform vec3 uSkyColor;
        uniform vec3 uGroundColor;
        uniform vec4 uHighlight;
        uniform int uOpaque;
        uniform int uWater;
        uniform int uSceneCopy;
        uniform sampler2D uSceneColor;
        uniform sampler2D uSceneDepth;
        uniform mat4 uInvProj;
        uniform int uDepthZeroToOne;
        uniform int uShadows;
        uniform mat4 uShadowMatrix;
        uniform sampler2DShadow uShadowMap;
        uniform float uShadowTexel;
        uniform int uShimmer;
        uniform int uUnlit;
        uniform int uTerrainDetail;
        uniform sampler2D uWeights;
        uniform sampler2D uLayer0;
        uniform sampler2D uLayer1;
        uniform sampler2D uLayer2;
        uniform sampler2D uLayer3;
        uniform vec4 uLayerTiling;
        uniform vec3 uLayerMean0;
        uniform vec3 uLayerMean1;
        uniform vec3 uLayerMean2;
        uniform vec3 uLayerMean3;
        uniform sampler2D uLayerNhr0;
        uniform sampler2D uLayerNhr1;
        uniform sampler2D uLayerNhr2;
        uniform sampler2D uLayerNhr3;
        uniform vec4 uLayerHasNhr;

        layout(location = 0) out vec4 oColor;
        """ + "\n" + Atmosphere + "\n" + """
        // Sun visibility at this fragment: 3x3 filtered taps of the shadow map, pushed off the surface along the normal by
        // a texel and a half (no acne on the terrain), fading out toward the edge of the shadowed square.
        float sunShadow(vec3 n)
        {
            if (uShadows == 0)
            {
                return 1.0;
            }

            vec3 c = (uShadowMatrix * vec4(vWorld + n * (uShadowTexel * 1.5), 1.0)).xyz * 0.5 + 0.5;
            float edge = max(abs(c.x - 0.5), abs(c.y - 0.5)) * 2.0;
            if (edge >= 1.0 || c.z >= 1.0)
            {
                return 1.0;
            }

            vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
            float sum = 0.0;
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    sum += texture(uShadowMap, vec3(c.xy + vec2(x, y) * texel, c.z));
                }
            }

            return mix(sum / 9.0, 1.0, smoothstep(0.8, 1.0, edge));
        }

        // Normal mapping without precomputed tangents (Christian Schüler, "Followup: Normal Mapping Without Precomputed
        // Tangents", 2013): the cotangent frame of the surface from the screen derivatives of position and UV. UE normal
        // maps are DirectX style (green = +V), which is exactly this frame's B axis.
        vec3 perturbNormal(vec3 n, vec3 dpx, vec3 dpy, vec2 duvx, vec2 duvy, vec2 xy)
        {
            vec3 dp2perp = cross(dpy, n);
            vec3 dp1perp = cross(n, dpx);
            vec3 t = dp2perp * duvx.x + dp1perp * duvy.x;
            vec3 b = dp2perp * duvx.y + dp1perp * duvy.y;
            float scale = inversesqrt(max(max(dot(t, t), dot(b, b)), 1e-30));
            float z = sqrt(max(1.0 - dot(xy, xy), 0.0));
            return normalize((t * xy.x + b * xy.y) * scale + n * z);
        }

        // A layer texture sampled about nine times larger, as a brightness factor around 1 (hue kept): breaks up its repeat.
        float Macro(vec3 large, vec3 mean)
        {
            const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
            return mix(1.0, dot(large, luma) / max(dot(mean, luma), 0.01), 0.6);
        }

        void main()
        {
            // Derivatives first, while every pixel of the quad is still running (masked texels are discarded below).
            vec3 dpx = dFdx(vView);
            vec3 dpy = dFdy(vView);
            vec2 duvx = dFdx(vUv);
            vec2 duvy = dFdy(vUv);
            vec4 albedo = vTint * uSectionTint; // node tint x material colour
            vec2 groundBump = vec2(0.0); // terrain: the paint layers' normal maps (tangent X, Y along world X, Z)
            float paintMask = 1.0;
            if (uHasTexture != 0)
            {
                vec4 texel = texture(uTexture, vUv);
                albedo = vTint * vec4(min(uSectionTint.rgb * texel.rgb, vec3(1.0)), uSectionTint.a * texel.a); // UE clamps base colour to 1
                paintMask = texel.a; // shiny paint: the texture's alpha says where the paint is
                // Masked material: leaves and grass are cut out of their cards. Far mips average the leaves with the gaps
                // between them, so alpha is sharpened with the mip level and a distant forest keeps its leaves.
                if (uAlphaCutoff > 0.0 && albedo.a * (1.0 + max(textureQueryLod(uTexture, vUv).x, 0.0) * 0.25) < uAlphaCutoff)
                {
                    discard;
                }

                if (uTerrainDetail != 0)
                {
                    // Ground up close: the paint layers' own textures, tiled in world space, sharpen the baked colour. Each
                    // is also sampled about nine times larger, which breaks up the repeat of its tile, and its normal map
                    // (NHR: normal X, Y, height, roughness) gives pebbles, needles and ruts their relief in the light.
                    float fade = 1.0 - smoothstep(6000.0, 30000.0, length(uCameraPosition - vWorld));
                    if (fade > 0.0)
                    {
                        vec4 w = texture(uWeights, vUv);
                        vec2 ue = vec2(vWorld.x, vWorld.z);
                        vec3 detail = vec3(0.0);
                        vec3 mean = vec3(0.0);
                        vec2 bump = vec2(0.0);
                        const float macro = 0.113;
                        if (uLayerTiling.x > 0.0)
                        {
                            vec2 t = ue * uLayerTiling.x;
                            detail += w.x * texture(uLayer0, t).rgb * Macro(texture(uLayer0, t.yx * macro).rgb, uLayerMean0);
                            mean += w.x * uLayerMean0;
                            bump += w.x * uLayerHasNhr.x * (texture(uLayerNhr0, t).rg * 2.0 - 1.0);
                        }

                        if (uLayerTiling.y > 0.0)
                        {
                            vec2 t = ue * uLayerTiling.y;
                            detail += w.y * texture(uLayer1, t).rgb * Macro(texture(uLayer1, t.yx * macro).rgb, uLayerMean1);
                            mean += w.y * uLayerMean1;
                            bump += w.y * uLayerHasNhr.y * (texture(uLayerNhr1, t).rg * 2.0 - 1.0);
                        }

                        if (uLayerTiling.z > 0.0)
                        {
                            vec2 t = ue * uLayerTiling.z;
                            detail += w.z * texture(uLayer2, t).rgb * Macro(texture(uLayer2, t.yx * macro).rgb, uLayerMean2);
                            mean += w.z * uLayerMean2;
                            bump += w.z * uLayerHasNhr.z * (texture(uLayerNhr2, t).rg * 2.0 - 1.0);
                        }

                        if (uLayerTiling.w > 0.0)
                        {
                            vec2 t = ue * uLayerTiling.w;
                            detail += w.w * texture(uLayer3, t).rgb * Macro(texture(uLayer3, t.yx * macro).rgb, uLayerMean3);
                            mean += w.w * uLayerMean3;
                            bump += w.w * uLayerHasNhr.w * (texture(uLayerNhr3, t).rg * 2.0 - 1.0);
                        }

                        float covered = dot(w, vec4(1.0));
                        if (dot(mean, vec3(1.0)) > 0.01)
                        {
                            vec3 ratio = clamp(detail / max(mean, vec3(0.01)), 0.3, 2.6);
                            albedo.rgb *= mix(vec3(1.0), ratio, fade);
                            groundBump = bump / max(covered, 1e-3) * fade;
                        }
                    }
                }
            }

            vec3 toCamera = normalize(uCameraPosition - vWorld);
            vec3 n = vNormal;
            float len = length(n);
            n = len > 1e-8 ? n / len : toCamera;
            // Two-sided: always shade the side facing the camera, whatever the winding.
            if (dot(n, toCamera) < 0.0)
            {
                n = -n;
            }

            // Opaque and masked surfaces cover the pixel: a texture's alpha (a far leaf mip, a spec mask) must not let the
            // window behind the view show through (distant trees came out white). Stand-ins (spawn models) glint: their
            // opacity breathes a fifth either way over 1.5 s.
            float alpha = uOpaque != 0 ? 1.0 : albedo.a * (uShimmer != 0 ? 1.0 + 0.2 * sin(uTime * 4.1887902) : 1.0);
            vec3 geometric = n;
            if (uHasNormalMap != 0 && uHasTexture != 0)
            {
                n = perturbNormal(n, dpx, dpy, duvx, duvy, texture(uNormalMap, vUv).rg * 2.0 - 1.0);
            }

            if (dot(groundBump, groundBump) > 0.0)
            {
                // The ground's tangent frame is the world's X and Z laid onto the surface (UE's world-aligned layer UVs).
                vec3 t = normalize(vec3(1.0, 0.0, 0.0) - n * n.x);
                vec3 b = normalize(vec3(0.0, 0.0, 1.0) - n * n.z);
                n = normalize(t * groundBump.x + b * groundBump.y + n * sqrt(max(1.0 - dot(groundBump, groundBump), 0.0)));
            }

            vec3 hemi = mix(uGroundColor, uSkyColor, n.y * 0.5 + 0.5);
            float key = max(dot(n, -uLightDirection), 0.0);
            key *= key > 0.0 ? sunShadow(geometric) : 1.0;
            vec3 lit = albedo.rgb * (hemi + uLightColor * key);
            if (uRoughness.y > 0.0 && uHasTexture != 0)
            {
                // The sun's highlight on a dielectric (F0 = 0.04): GGX distribution, Schlick Fresnel and the implicit
                // geometry term; roughness from the texture's alpha scaled into the material's range.
                float rough = clamp(mix(uRoughness.x, uRoughness.y, paintMask), 0.15, 1.0);
                float a2 = rough * rough * rough * rough;
                vec3 h = normalize(toCamera - uLightDirection);
                float nh = max(dot(n, h), 0.0);
                float d = nh * nh * (a2 - 1.0) + 1.0;
                float fresnel = 0.04 + 0.96 * pow(1.0 - max(dot(h, toCamera), 0.0), 5.0);
                lit += uLightColor * (key * fresnel * 0.25 * a2 / (3.14159265 * d * d));
            }
            if (uWater != 0)
            {
                // Water: the tint is the colour of deep water. Swells and small waves bend the normal (the small ones fade
                // out with the pixel's footprint and leave their roughness to the sun's glitter), Fresnel mixes in the
                // sky's reflection. With the scene copy, the ground below shows through by the water's thickness: clear
                // turquoise in the shallows, opaque blue where it is deep, a light foam line where it meets the shore.
                float footprint = length(fwidth(vWorld.xz)); // world size of a pixel on the water (a uniform branch)
                vec4 wave = waterNormal(vWorld.xz, footprint);
                n = toCamera.y < 0.0 ? -wave.xyz : wave.xyz;
                float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, toCamera), 0.0), 5.0);
                vec3 r = reflect(-toCamera, n);
                r.y = abs(r.y);
                float shine = mix(1400.0, 24.0, wave.w);
                float glint = pow(max(dot(r, -uLightDirection), 0.0), shine) * shine * 0.012;
                vec3 sunlight = uLightColor * max(-uLightDirection.y, 0.0);
                // Light scattered back out of deep water: brighter on the slopes facing the sun (the waves read from above)
                // and in kilometre-wide patches (wind on the water), so a calm sea is not one flat colour from high up.
                float patches = 0.5 + 0.5 * sin(dot(vWorld.xz, vec2(0.00004, 0.00009))) * sin(dot(vWorld.xz, vec2(0.00007, -0.00003)) + 2.0);
                vec3 deep = albedo.rgb * (uSkyColor * 0.8 + sunlight * 0.35) * (0.4 + 1.3 * max(dot(n, -uLightDirection), 0.0)) * (0.85 + 0.3 * patches);
                vec3 below = deep;
                if (uSceneCopy != 0)
                {
                    ivec2 pixel = ivec2(gl_FragCoord.xy);
                    float depth = texelFetch(uSceneDepth, pixel, 0).r;
                    float thickness = 1e7;
                    if (depth != (uDepthZeroToOne != 0 ? 0.0 : 1.0))
                    {
                        // The ground behind this pixel, back in view space from the depth buffer: the water's thickness
                        // along the ray is how much farther it is than the surface.
                        vec2 ndc = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0)) * 2.0 - 1.0;
                        vec4 behind = uInvProj * vec4(ndc, uDepthZeroToOne != 0 ? depth : depth * 2.0 - 1.0, 1.0);
                        thickness = max(length(behind.xyz / behind.w) - length(vView), 0.0);
                    }

                    // Red is absorbed in a couple of metres, blue in tens of metres (per cm): shallows turn turquoise.
                    vec3 transmit = exp(-vec3(0.0045, 0.0012, 0.0008) * thickness);
                    vec3 ground = inverseToneMap(decodeOutput(texelFetch(uSceneColor, pixel, 0).rgb));
                    below = ground * transmit + deep * (1.0 - transmit);
                    float shore = 1.0 - smoothstep(0.0, 70.0, thickness * abs(toCamera.y));
                    float lace = 0.55 + 0.45 * sin(dot(vWorld.xz, vec2(0.031, 0.017)) + uWaterTime * 1.3) * sin(dot(vWorld.xz, vec2(-0.013, 0.029)) - uWaterTime);
                    below = mix(below, (uSkyColor + sunlight) * 0.9, shore * lace * 0.7);
                    alpha = 1.0;
                }
                else
                {
                    alpha = mix(alpha, 1.0, clamp(fresnel + glint, 0.0, 1.0));
                }

                lit = mix(below, skyRadiance(r, false), fresnel) + uLightColor * glint;
            }

            float metal = vSurface.x * paintMask;
            float gloss = vSurface.y * paintMask;
            if (metal + gloss > 0.0)
            {
                // Studio reflection: dark floor, bright sky and a hot horizon band, so curved panels show a moving line.
                vec3 r = reflect(-toCamera, n);
                vec3 env = mix(uGroundColor * 0.5, uSkyColor * 1.9, smoothstep(-0.3, 0.45, r.y)) + vec3(1.4) * exp(-abs(r.y - 0.06) * 16.0);
                vec3 h = normalize(toCamera - uLightDirection);
                float nh = max(dot(n, h), 0.0);
                float fresnel = 0.04 + 0.96 * pow(1.0 - max(dot(n, toCamera), 0.0), 5.0);
                // Metal: the colour is in the reflection, not in the diffuse light. A bright reflection is scaled down as a
                // whole, not clipped per channel: clipped gold turns yellow on the brightest panels.
                vec3 reflection = albedo.rgb * 1.7 * (env + uLightColor * pow(nh, 60.0) * 4.0);
                reflection /= max(1.0, max(reflection.r, max(reflection.g, reflection.b)));
                lit = mix(lit, reflection, metal);
                // Clear coat: a colourless lacquer on top, strongest at grazing angles, with a tight highlight.
                lit += gloss * (fresnel * env * 0.6 + uLightColor * pow(nh, 400.0) * 6.0);
                lit /= max(1.0, max(lit.r, max(lit.g, lit.b)));
            }

            // Lighting off (Settings): each surface in its own colour, no sun, shade, haze or tone curve.
            lit = uUnlit != 0 ? albedo.rgb : toneMap(applyAtmosphere(lit, vWorld));
            if ((vFlags & 1u) != 0u)
            {
                // After tone mapping, so the selection keeps the UI accent whatever the light.
                float rim = pow(1.0 - max(dot(n, toCamera), 0.0), 2.0);
                lit = mix(lit, uHighlight.rgb, uHighlight.a) + uHighlight.rgb * rim * 0.8;
            }

            oColor = vec4(encodeOutput(lit), alpha);
        }
        """;

    /// <summary>
    /// Shared by the mesh and sky programs: the sky's colour along a view direction (horizon haze to zenith blue, the
    /// sun's glow and disc), height-aware distance haze that fades toward the horizon colour and brightens toward the
    /// sun, the sea's wave normals, an optional ACES-fit tone curve and the sRGB encode.
    /// </summary>
    private const string Atmosphere = """
        uniform vec3 uLightDirection;
        uniform vec3 uLightColor;
        uniform vec3 uCameraPosition;
        uniform int uEncodeSrgb;
        uniform vec3 uFogColor;
        uniform float uFogDensity;
        uniform float uFogFalloff;
        uniform int uSky;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform float uExposure;
        uniform float uTime;
        uniform float uWaterTime;
        uniform float uStars;

        vec3 linearToSrgb(vec3 c)
        {
            c = clamp(c, 0.0, 1.0);
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(hi, lo, vec3(lessThanEqual(c, vec3(0.0031308))));
        }

        vec3 encodeOutput(vec3 c)
        {
            return uEncodeSrgb != 0 ? linearToSrgb(c) : c;
        }

        // A colour as the target stores it, back to linear (the scene copy under water).
        vec3 decodeOutput(vec3 c)
        {
            return uEncodeSrgb != 0 ? mix(pow((c + 0.055) / 1.055, vec3(2.4)), c / 12.92, vec3(lessThanEqual(c, vec3(0.04045)))) : c;
        }

        // The colour before toneMap gave this one (its inverse; the curve is a ratio of quadratics).
        vec3 inverseToneMap(vec3 y)
        {
            if (uExposure <= 0.0)
            {
                return y;
            }

            y = clamp(y, 0.0, 0.98);
            vec3 a = 2.51 - 2.43 * y;
            vec3 b = 0.03 - 0.59 * y;
            vec3 c = -0.14 * y;
            return (-b + sqrt(b * b - 4.0 * a * c)) / (2.0 * a) / uExposure;
        }

        // Narkowicz's ACES filmic fit after an exposure; exposure 0 leaves the colour as it is.
        vec3 toneMap(vec3 c)
        {
            if (uExposure <= 0.0)
            {
                return c;
            }

            c *= uExposure;
            return clamp((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14), 0.0, 1.0);
        }

        // Light scattered toward the eye around the sun: a wide warm glow and a tighter halo.
        vec3 sunGlow(vec3 v)
        {
            float s = max(dot(v, -uLightDirection), 0.0);
            return uLightColor * (0.06 * pow(s, 5.0) + 0.22 * pow(s, 40.0));
        }

        // The sky along the unit direction v: the horizon's haze turns deeper blue toward the zenith; below the horizon
        // the haze darkens a little. The haze at the horizon is exactly the colour distant ground fades to.
        vec3 skyRadiance(vec3 v, bool disc)
        {
            float up = max(v.y, 0.0);
            vec3 c = mix(uSkyHorizon, uSkyZenith, 1.0 - exp(-up * 4.0)) * (1.0 - 0.2 * smoothstep(0.0, 0.3, -v.y));
            c += sunGlow(v) * (1.0 - 0.6 * up);
            if (disc)
            {
                // The sun's disc, or by night (the key light is then the moon) the moon's.
                c += uLightColor * 14.0 * smoothstep(0.99985, 0.99993, dot(v, -uLightDirection));
                if (uStars > 0.0 && v.y > 0.0)
                {
                    // Stars: one in a few hundred cells of a fine grid over the sky, each with its own brightness.
                    vec3 cell = floor(v * 300.0);
                    float h = fract(sin(dot(cell, vec3(127.1, 311.7, 74.7))) * 43758.5453);
                    float b = fract(h * 97.31);
                    c += vec3(0.8, 0.85, 1.0) * (step(0.9975, h) * (0.3 + 1.7 * b) * uStars * smoothstep(0.0, 0.15, v.y));
                }
            }

            return c;
        }

        vec3 hazeColor(vec3 v)
        {
            return uSky != 0 ? uSkyHorizon + sunGlow(v) : uFogColor;
        }

        // Exponential distance fog, thinning with height above sea level (y = 0) when uFogFalloff > 0: the density
        // uFogDensity * exp(-uFogFalloff * y) integrated along the ray from the camera.
        vec3 applyAtmosphere(vec3 c, vec3 world)
        {
            if (uFogDensity <= 0.0)
            {
                return c;
            }

            vec3 d = world - uCameraPosition;
            float dist = max(length(d), 1e-6);
            float depth = uFogDensity * dist;
            if (uFogFalloff > 0.0)
            {
                // Both exponents stay small or negative, so a camera far above the island cannot overflow.
                float camera = exp(-uFogFalloff * max(uCameraPosition.y, -1e5));
                float k = uFogFalloff * d.y;
                depth *= abs(k) > 1e-4 ? (camera - exp(-uFogFalloff * max(world.y, -1e5))) / k : camera;
            }

            return mix(c, hazeColor(d / dist), 1.0 - exp(-depth));
        }

        // Normal of the water at UE-plane point p (cm), and in w how much of the small waves the pixel's footprint has
        // filtered out (0 = all there, 1 = gone: the sun's glint then spreads into a wide glitter). Gravity waves at
        // deep-water speeds and spread-out headings: three long swells (80-400 m) that stay visible from kilometres up,
        // and ten short waves faded out once a pixel covers a good part of their wavelength, so far water does not flicker.
        vec4 waterNormal(vec2 p, float footprint)
        {
            vec2 g = vec2(0.0);
            float lost = 0.0;
            float swell = 0.7 + 0.3 * sin(dot(p, vec2(0.00011, 0.00007)) + 1.3) * sin(dot(p, vec2(-0.00005, 0.00013)));
            for (int i = 0; i < 3; i++)
            {
                float a = float(i) * 2.1 + 0.3;
                vec2 dir = vec2(cos(a), sin(a));
                float wavelength = 40000.0 * pow(0.45, float(i));
                float k = 6.2831853 / wavelength;
                g += dir * (0.03 * (1.0 - smoothstep(0.08, 0.35, footprint / wavelength))) * cos(dot(dir, p) * k - sqrt(981.0 * k) * uWaterTime + float(i));
            }

            for (int i = 0; i < 10; i++)
            {
                float a = float(i) * 2.39996 + 0.7;
                vec2 dir = vec2(cos(a), sin(a));
                float wavelength = 3000.0 * pow(0.72, float(i));
                float k = 6.2831853 / wavelength;
                float fade = 1.0 - smoothstep(0.08, 0.35, footprint / wavelength);
                g += dir * (0.035 * swell * fade) * cos(dot(dir, p) * k - sqrt(981.0 * k) * uWaterTime + float(i) * 1.7);
                lost += (1.0 - fade) * 0.1;
            }

            return vec4(normalize(vec3(-g.x, 1.0, -g.y)), lost * swell);
        }
        """;

    /// <summary>Sky pass: the full-screen triangle of <see cref="GridVertex"/>, drawn only where the depth buffer is still clear.</summary>
    public const string SkyFragment = """
        #version 430 core
        in vec3 vNear;
        in vec3 vFar;
        uniform float uFarDepth;
        layout(location = 0) out vec4 oColor;
        """ + "\n" + Atmosphere + "\n" + """
        void main()
        {
            gl_FragDepth = uFarDepth;
            oColor = vec4(encodeOutput(toneMap(skyRadiance(normalize(vFar - vNear), true))), 1.0);
        }
        """;

    /// <summary>Shadow pass: depth only; masked sections (leaves) cut their holes so they cast the leaves' shape.</summary>
    public const string ShadowFragment = """
        #version 430 core
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform float uAlphaCutoff;

        void main()
        {
            if (uAlphaCutoff > 0.0 && texture(uTexture, vUv).a < uAlphaCutoff)
            {
                discard;
            }
        }
        """;

    /// <summary>ID pass: writes the per-instance pick code to an R32UI attachment.</summary>
    public const string PickFragment = """
        #version 430 core
        flat in uint vPickId;
        layout(location = 0) out uint oId;

        void main()
        {
            oId = vPickId;
        }
        """;

    /// <summary>Infinite ground grid: full-screen triangle, ray/plane intersection, depth-correct via gl_FragDepth.</summary>
    public const string GridVertex = """
        #version 430 core
        uniform mat4 uInvViewProj;
        out vec3 vNear;
        out vec3 vFar;

        vec3 unproject(vec2 xy, float z)
        {
            vec4 p = uInvViewProj * vec4(xy, z, 1.0);
            return p.xyz / p.w;
        }

        void main()
        {
            vec2 pos = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
            vNear = unproject(pos, -1.0);
            vFar = unproject(pos, 1.0);
            gl_Position = vec4(pos, 0.0, 1.0);
        }
        """;

    /// <summary>Grid fragment shader.</summary>
    public const string GridFragment = """
        #version 430 core
        in vec3 vNear;
        in vec3 vFar;

        uniform mat4 uViewProj;
        uniform int uDepthZeroToOne;
        uniform float uHeight;
        uniform float uCellSize;
        uniform float uMajorEvery;
        uniform float uFadeDistance;
        uniform vec3 uCameraPosition;
        uniform vec4 uMinorColor;
        uniform vec4 uMajorColor;
        uniform vec4 uAxisXColor;
        uniform vec4 uAxisZColor;
        uniform int uEncodeSrgb;

        layout(location = 0) out vec4 oColor;

        vec3 linearToSrgb(vec3 c)
        {
            c = clamp(c, 0.0, 1.0);
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(hi, lo, vec3(lessThanEqual(c, vec3(0.0031308))));
        }

        float lineCoverage(vec2 coord)
        {
            vec2 d = max(fwidth(coord), vec2(1e-6));
            vec2 g = abs(fract(coord - 0.5) - 0.5) / d;
            float density = max(d.x, d.y);
            return (1.0 - min(min(g.x, g.y), 1.0)) * (1.0 - smoothstep(0.15, 0.5, density));
        }

        void main()
        {
            float dy = vFar.y - vNear.y;
            if (abs(dy) < 1e-9)
            {
                discard;
            }

            float t = (uHeight - vNear.y) / dy;
            if (t < 0.0 || t > 1.0)
            {
                discard;
            }

            vec3 p = mix(vNear, vFar, t);
            vec2 cell = p.xz / uCellSize;
            float minor = lineCoverage(cell) * uMinorColor.a;
            float major = lineCoverage(cell / uMajorEvery) * uMajorColor.a;
            vec3 color = major >= minor ? uMajorColor.rgb : uMinorColor.rgb;
            float alpha = max(minor, major);

            float wz = max(fwidth(p.z), 1e-6);
            float wx = max(fwidth(p.x), 1e-6);
            float axisX = (1.0 - min(abs(p.z) / (wz * 1.5), 1.0)) * uAxisXColor.a;
            float axisZ = (1.0 - min(abs(p.x) / (wx * 1.5), 1.0)) * uAxisZColor.a;
            if (axisX > alpha) { color = uAxisXColor.rgb; alpha = axisX; }
            if (axisZ > alpha) { color = uAxisZColor.rgb; alpha = axisZ; }

            float dist = length(p.xz - uCameraPosition.xz);
            alpha *= 1.0 - smoothstep(uFadeDistance * 0.4, uFadeDistance, dist);
            if (alpha < 0.004)
            {
                discard;
            }

            vec4 clip = uViewProj * vec4(p, 1.0);
            float ndcZ = clip.z / clip.w;
            gl_FragDepth = clamp(uDepthZeroToOne != 0 ? ndcZ : ndcZ * 0.5 + 0.5, 0.0, 1.0);
            oColor = vec4(uEncodeSrgb != 0 ? linearToSrgb(color) : color, alpha);
        }
        """;

    /// <summary>Overlay line vertex shader: world position + linear colour per vertex.</summary>
    public const string LineVertex = """
        #version 430 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec4 aColor;
        uniform mat4 uViewProj;
        out vec4 vColor;

        void main()
        {
            vColor = aColor;
            gl_Position = uViewProj * vec4(aPosition, 1.0);
        }
        """;

    /// <summary>Overlay line fragment shader.</summary>
    public const string LineFragment = """
        #version 430 core
        in vec4 vColor;
        uniform int uEncodeSrgb;
        layout(location = 0) out vec4 oColor;

        vec3 linearToSrgb(vec3 c)
        {
            c = clamp(c, 0.0, 1.0);
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(hi, lo, vec3(lessThanEqual(c, vec3(0.0031308))));
        }

        void main()
        {
            vec3 rgb = uEncodeSrgb == 1 ? linearToSrgb(vColor.rgb) : vColor.rgb;
            oColor = vec4(rgb, vColor.a);
        }
        """;

    /// <summary>
    /// Overlay stroke vertex shader: each line is a screen-space quad (6 vertices) of <c>aCorner.z</c> pixels, with
    /// square caps so polylines join without gaps; <c>vEdge</c> is the signed distance from the centre line in pixels.
    /// </summary>
    public const string StrokeVertex = """
        #version 430 core
        layout(location = 0) in vec3 aStart;
        layout(location = 1) in vec3 aEnd;
        layout(location = 2) in vec4 aColor;
        layout(location = 3) in vec3 aCorner; // x: 0 = start, 1 = end; y: side -1 / +1; z: width in pixels
        layout(location = 4) in vec3 aPrev; // the polyline point before the start (the start itself: a square cap)
        layout(location = 5) in vec3 aNext; // the polyline point after the end (the end itself: a square cap)
        uniform mat4 uViewProj;
        uniform vec2 uViewport;
        out vec4 vColor;
        noperspective out float vEdge;
        flat out float vHalf;

        void main()
        {
            vColor = aColor;
            vec4 a = uViewProj * vec4(aStart, 1.0);
            vec4 b = uViewProj * vec4(aEnd, 1.0);
            if (a.w <= 1e-4 || b.w <= 1e-4)
            {
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0); // an end behind the camera: the line is not drawn
                vEdge = 0.0;
                vHalf = 0.0;
                return;
            }

            vec2 halfView = 0.5 * uViewport;
            vec2 sa = a.xy / a.w * halfView;
            vec2 sb = b.xy / b.w * halfView;
            vec2 d = sb - sa;
            float len = length(d);
            vec2 dir = len > 1e-3 ? d / len : vec2(1.0, 0.0);
            vec2 nrm = vec2(-dir.y, dir.x);
            float reach = aCorner.z * 0.5 + 1.0; // one pixel more for the anti-aliased edge
            bool atEnd = aCorner.x > 0.5;
            vec4 p = atEnd ? b : a;
            vec2 sp = atEnd ? sb : sa;
            vec3 point = atEnd ? aEnd : aStart;
            vec3 neighbour = atEnd ? aNext : aPrev;
            vec4 o = uViewProj * vec4(neighbour, 1.0);
            if (any(notEqual(neighbour, point)) && o.w > 1e-4)
            {
                // A polyline joint: the corner sits on the bisector of the two segments (a miter), so consecutive
                // segments tile without overlap and a translucent polyline stays even. Sharper than 120 degrees: no miter.
                vec2 so = o.xy / o.w * halfView;
                vec2 d2 = atEnd ? so - sb : sa - so;
                float len2 = length(d2);
                vec2 dir2 = len2 > 1e-3 ? d2 / len2 : dir;
                vec2 nrm2 = vec2(-dir2.y, dir2.x);
                float c = 1.0 + dot(nrm, nrm2);
                sp += aCorner.y * (c > 0.5 ? (nrm + nrm2) * (reach / c) : nrm * reach);
            }
            else
            {
                sp += nrm * (aCorner.y * reach) + dir * (atEnd ? reach : -reach);
            }

            gl_Position = vec4(sp / halfView * p.w, p.z, p.w);
            vEdge = aCorner.y * reach;
            vHalf = aCorner.z * 0.5;
        }
        """;

    /// <summary>Overlay stroke fragment shader: the colour, faded over the last pixel of each edge.</summary>
    public const string StrokeFragment = """
        #version 430 core
        in vec4 vColor;
        noperspective in float vEdge;
        flat in float vHalf;
        uniform int uEncodeSrgb;
        layout(location = 0) out vec4 oColor;

        vec3 linearToSrgb(vec3 c)
        {
            c = clamp(c, 0.0, 1.0);
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(hi, lo, vec3(lessThanEqual(c, vec3(0.0031308))));
        }

        void main()
        {
            float coverage = clamp(vHalf + 0.5 - abs(vEdge), 0.0, 1.0);
            vec3 rgb = uEncodeSrgb == 1 ? linearToSrgb(vColor.rgb) : vColor.rgb;
            oColor = vec4(rgb, vColor.a * coverage);
        }
        """;
}
