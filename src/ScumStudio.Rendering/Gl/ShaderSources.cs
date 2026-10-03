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

        out vec3 vNormal;
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
            vUv = aUv;
            vWorld = world.xyz;
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
        in vec4 vTint;
        flat in uint vPickId;
        flat in uint vFlags;
        flat in vec2 vSurface;

        uniform sampler2D uTexture;
        uniform int uHasTexture;
        uniform float uAlphaCutoff;
        uniform vec4 uSectionTint;
        uniform vec3 uSkyColor;
        uniform vec3 uGroundColor;
        uniform vec3 uLightDirection;
        uniform vec3 uLightColor;
        uniform vec4 uHighlight;
        uniform vec3 uCameraPosition;
        uniform int uEncodeSrgb;
        uniform vec3 uFogColor;
        uniform float uFogDensity;
        uniform int uOpaque;
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
            vec4 albedo = vTint * uSectionTint; // node tint x material colour
            float paintMask = 1.0;
            if (uHasTexture != 0)
            {
                vec4 texel = texture(uTexture, vUv);
                albedo *= texel;
                paintMask = texel.a; // shiny paint: the texture's alpha says where the paint is
                // Masked material: leaves and grass are cut out of their cards. Far mips average the leaves with the gaps
                // between them, so alpha is sharpened with the mip level and a distant forest keeps its leaves.
                if (uAlphaCutoff > 0.0 && albedo.a * (1.0 + max(textureQueryLod(uTexture, vUv).x, 0.0) * 0.25) < uAlphaCutoff)
                {
                    discard;
                }

                if (uTerrainDetail != 0)
                {
                    // Ground up close: the paint layers' own textures, tiled in world space, sharpen the baked colour.
                    float fade = 1.0 - smoothstep(4000.0, 16000.0, length(uCameraPosition - vWorld));
                    if (fade > 0.0)
                    {
                        vec4 w = texture(uWeights, vUv);
                        vec2 ue = vec2(vWorld.x, vWorld.z);
                        vec3 detail = vec3(0.0);
                        vec3 mean = vec3(0.0);
                        if (uLayerTiling.x > 0.0) { detail += w.x * texture(uLayer0, ue * uLayerTiling.x).rgb; mean += w.x * uLayerMean0; }
                        if (uLayerTiling.y > 0.0) { detail += w.y * texture(uLayer1, ue * uLayerTiling.y).rgb; mean += w.y * uLayerMean1; }
                        if (uLayerTiling.z > 0.0) { detail += w.z * texture(uLayer2, ue * uLayerTiling.z).rgb; mean += w.z * uLayerMean2; }
                        if (uLayerTiling.w > 0.0) { detail += w.w * texture(uLayer3, ue * uLayerTiling.w).rgb; mean += w.w * uLayerMean3; }
                        if (dot(mean, vec3(1.0)) > 0.01)
                        {
                            vec3 ratio = clamp(detail / max(mean, vec3(0.01)), 0.35, 2.4);
                            albedo.rgb *= mix(vec3(1.0), ratio, fade);
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

            vec3 hemi = mix(uGroundColor, uSkyColor, n.y * 0.5 + 0.5);
            float key = max(dot(n, -uLightDirection), 0.0);
            vec3 lit = albedo.rgb * (hemi + uLightColor * key);
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

            if (uFogDensity > 0.0)
            {
                float fog = 1.0 - exp(-uFogDensity * length(uCameraPosition - vWorld));
                lit = mix(lit, uFogColor, fog);
            }

            if ((vFlags & 1u) != 0u)
            {
                float rim = pow(1.0 - max(dot(n, toCamera), 0.0), 2.0);
                lit = mix(lit, uHighlight.rgb, uHighlight.a) + uHighlight.rgb * rim * 0.8;
            }

            // Opaque and masked surfaces cover the pixel: a texture's alpha (a far leaf mip, a spec mask) must not let the
            // window behind the view show through (distant trees came out white).
            oColor = vec4(uEncodeSrgb != 0 ? linearToSrgb(lit) : lit, uOpaque != 0 ? 1.0 : albedo.a);
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
}
