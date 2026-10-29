# pmx-to-vrm

`pmx2vrm` is a command-line converter from MikuMikuDance models (`.pmx`, PMX 2.0 / 2.1) to **VRM 1.0**
(`.vrm`: glTF 2.0 binary with `VRMC_vrm`, `VRMC_springBone` and `VRMC_materials_mtoon`).

It converts:

- the mesh, the skin weights and the textures,
- MMD materials, approximated with MToon,
- the humanoid bones,
- the standard mouth, blink and emotion morphs, as VRM expressions,
- the **MMD physics**: rigid bodies and joints become VRM spring bones and colliders, so hair, skirts and ribbons sway.

The tool is a plain .NET 10 console app with no third-party packages. Every reader and writer was written from the public format documentation: the PMX 2.0/2.1 format description, glTF 2.0 and the [VRM 1.0 specification](https://github.com/vrm-c/vrm-specification).

## Build

```
dotnet build -c Release src/Pmx2Vrm
# or a single executable:
dotnet publish src/Pmx2Vrm -c Release -o out
```

## Usage

```
pmx2vrm <in.pmx> [out.vrm] [options]
pmx2vrm <folder> [out-folder] [options]     converts every .pmx below the folder
```

| option | default | |
|---|---|---|
| `--name <text>` | PMX model name | avatar name. Ignored in folder mode |
| `--author <text>` | guessed from the PMX comment (`モデル製作：…`, `Model: …`), else `unknown` | |
| `--license-url <url>` | `https://vrm.dev/licenses/1.0/` | |
| `--avatar-permission` | `onlyAuthor` | `onlyAuthor` / `onlySeparatelyLicensedPerson` / `everyone` |
| `--commercial` | `personalNonProfit` | `personalNonProfit` / `personalProfit` / `corporation` |
| `--modification` | `prohibited` | `prohibited` / `allowModification` / `allowModificationRedistribution` |
| `--allow-redistribution` | off | sets `allowRedistribution: true` |
| `--scale <m>` | `0.08` | metres per MMD unit; a typical character comes out 1.5 to 1.8 m tall |
| `--no-tpose` | | keeps MMD's A-pose instead of straightening the arms |
| `--no-physics` | | writes no spring bones or colliders |
| `--all-morphs` | | exports every vertex and group morph as a morph target (plus a custom expression for each) |
| `--quiet` | | prints only errors |

Example:

```
dotnet run --project src/Pmx2Vrm -- "C:\models\Foo\Foo.pmx" "C:\out\Foo.vrm" --author "Original Author"
```

Each conversion prints a short report: bones mapped, expressions, spring chains, colliders and warnings. It also writes **`<name>.license.txt`** next to the output, containing the PMX comment and any readme or terms file (`readme*.txt`, `規約*.txt`, ...) found in the model's folder.

## How the conversion works

### Space

MMD models are left-handed, measured in "units" of about 8 cm, and face -Z. The tool scales positions by `--scale` and mirrors Z, so the model faces +Z as VRM 1.0 requires. Mirroring the geometry keeps the rendered image the same, so triangle winding is reversed to turn DirectX's clockwise front faces into glTF's counter-clockwise ones. Finally the model is moved so its lowest vertex sits on y = 0.

### Rest pose

Every glTF node gets a translation only: no rotation, no scale.

With the default T-pose, the upper arm, forearm and hand are each rotated about their own joint until they point along +X (left arm) or -X (right arm). The rotation is baked into every descendant bone, every vertex (blended by its weights), every morph offset and every rigid body. The inverse bind matrices are then pure translations, so the skin is the identity at rest.

MMD's `センター` (the hips) usually sits near the knees. Its node is moved to between the legs and the spine. Only the pivot moves: the skin is unaffected.

### Mesh and materials

- The mesh has one primitive per PMX material. All primitives share one vertex buffer and one set of morph targets.
- Skinning uses up to 4 joints per vertex. BDEF1/2/4 are converted directly. **SDEF is converted as BDEF2** (linear) and QDEF as BDEF4.
- Every bone becomes a skin joint, including IK, twist and helper bones.
- Textures are embedded. PNG and JPEG are copied as they are. BMP, TGA and DDS (uncompressed or DXT1/3/5) are decoded and re-encoded as PNG. Paths are resolved relative to the `.pmx`, ignoring case.
- MToon settings:
  - **Base color** = texture × MMD diffuse, brightened by ambient the way MMD's default light does.
  - **Shade color** = base × the dark end of the toon ramp. A model's own toon texture is sampled. MMD's shared `toon01-10` are approximated.
  - **Alpha**: diffuse alpha below 1 gives `BLEND`. Otherwise the texture's alpha decides: hard-edged alpha gives `MASK` (cutoff 0.5) and soft alpha gives `BLEND`. `BLEND` materials write depth (`transparentWithZWrite`), as MMD does, and are drawn in PMX order.
  - The double-sided flag becomes `doubleSided`.
  - The edge flag, size and color become an MToon world-space outline.
  - Additive sphere maps (`.spa`) become the MToon matcap. Multiplicative sphere maps (`.sph`) are skipped.

### Humanoid

The tool matches MMD's standard Japanese bone names, plus common English names. Names are normalized first (NFKC, so `人指１` = `人指1` and `ｳｨﾝｸ` = `ウィンク`):

| VRM | MMD |
|---|---|
| hips | the deepest common ancestor of 上半身 and both legs (usually センター, or グルーブ / 腰) |
| spine / chest / upperChest | 上半身 / 上半身2 / 上半身3 |
| neck / head / eyes | 首 / 頭 / 左目, 右目 |
| shoulder / upperArm / lowerArm / hand | 左肩 / 左腕 / 左ひじ / 左手首 |
| upperLeg / lowerLeg / foot / toes | **左足D / 左ひざD / 左足首D / 左足先EX** when present (the D-bones carry the weights), else 左足 / 左ひざ / 左足首 / 左つま先 |
| thumb | 親指０/１/２ → metacarpal/proximal/distal. Models without 親指０ use 親指１/２ as metacarpal/proximal |
| index / middle / ring / little | 人指, 中指, 薬指, 小指 １/２/３ |

Rules:

- Each human bone must sit below its humanoid parent, as the VRM parent table requires. A bone that doesn't is left out, with a warning.
- Bones with simulated rigid bodies are never human bones.
- Twist, IK and helper bones stay as ordinary skinned nodes.

`lookAt` is the bone type, with its offset from the head to between the eyes. `firstPerson` marks the mesh as `auto`.

### Expressions

| VRM | MMD morph |
|---|---|
| aa / ih / ou / ee / oh | あ / い / う / え / お |
| blink | まばたき |
| blinkLeft / blinkRight | ウィンク２ / ウィンク２右 (falls back to ウィンク / ウィンク右). **The side is checked from the vertices the morph moves** (the model's left is +X) |
| happy / angry / sad / relaxed / surprised | 笑い / 怒り / 困る / にこり (にっこり) / びっくり |

Common English names (`a`, `blink`, `smile`, `wink_R`, ...) also match.

- Vertex morphs become morph targets.
- Group morphs are expanded with their influences into one target.
- Material morphs that change the diffuse color become `materialColorBinds` of type `color`. A material that an expression fades out is switched from `OPAQUE` to `MASK`, so the fade can hide it.
- Bone, UV, flip and impulse morph parts are skipped, with a warning.

### Physics → spring bones

| PMX | VRM |
|---|---|
| rigid body, mode 0 (follows its bone) on a body bone | **collider** on that bone. Sphere → sphere, capsule → capsule, box → capsule along its longest axis (radius = mean of the other two half extents) |
| rigid body, mode 1 / 2 (physics, physics + bone) | its bone becomes a **spring joint** |
| a chain of simulated bones in the bone hierarchy | one **spring**: from the first simulated bone down through its simulated children. At a branch, the deepest branch continues and each other branch starts its own spring. The chain ends on the next (non-simulated) child bone, or on a new `<bone>_end` node placed along the PMX bone's tail |
| PMX collision groups and masks | a spring uses a collider group if any of its bodies would collide with any collider in that group in MMD |

Colliders are grouped by the human bone they hang from: `Head`, `Body`, `LeftArm`, `RightArm`, `LeftLeg`, `RightLeg`. Hair therefore usually collides with Head and Body, and skirts with the legs, exactly as the model's author set the collision masks. Mode-0 "anchor" bodies on non-human bones that hold physics chains (skirt or hair roots) are not colliders.

Joint parameters:

- `stiffness` = `0.6 / (mean rotation range of the PMX joint, in radians, at least 0.15) + (mean rotation spring) / 40`, clamped to 0.4 to 4. Tight limits and strong springs give stiff joints.
- `dragForce` = `0.8 × mean(linear damping, angular damping)`, clamped to 0.2 to 0.9.
- `gravityPower` = `0.05 + 0.1 × mass` (mass capped at 2), clamped to 0 to 0.3. `gravityDir` is (0, -1, 0).
- `hitRadius` = the body's radius (for a box, the mean of its two smaller half extents), clamped to 5 mm to 12 cm.

The report lists every spring: its joint count, the human bone it hangs from and the collider groups it uses. It also warns when joints start inside a collider they collide with.

## Limitations

- **Spring bones are not Bullet.**
  - VRM springs are per-chain verlet joints. MMD's joints between neighbouring chains, such as the rings around a skirt, have no VRM equivalent and are dropped. Skirts sway strand by strand.
  - Joint position limits and translation springs are ignored.
  - The parameter mapping is a heuristic: tune it with the formulas above if a model is too floppy or too stiff.
- MMD bone features VRM doesn't have are lost: append/grant (付与) rotations, IK, fixed and local axes, external parents. Twist bones follow their parent and don't twist.
- SDEF is skinned linearly.
- The MToon look is an approximation of MMD's shading:
  - The shared toon01-10 ramps are approximated.
  - Multiplicative sphere maps are skipped.
  - Vertex edge scale is ignored.
- UV and bone morphs are not converted.
- PMX 2.1 soft bodies are ignored. PMD (the older format) is not read.
- The T-pose bake straightens the arms only. The legs are left as modelled.
- The tool does not produce a thumbnail.

## License of converted models

The tool's own license is set by the repository owner (no `LICENSE` file yet). **The models you convert keep their own terms**: a converted VRM is a modification of someone else's work and keeps the original model's terms of use. Read each model's readme and terms, which are copied into `<name>.license.txt`. Then choose the VRM meta flags (`--avatar-permission`, `--modification`, `--allow-redistribution`, `--commercial`) so they match what the original author allows. The defaults are the most restrictive. Many MMD models forbid redistribution, conversion to other formats or use outside MMD.

## Development

```
dotnet run --project tests/Pmx2Vrm.Tests            # all tests
dotnet run --project tests/Pmx2Vrm.Tests -- Physics # tests whose name contains "Physics"
```

The tests build a small MMD-style humanoid in memory and write it with a test-only PMX writer. It has standard bone names, A-pose arms, a D-bone leg, a physics hair chain, a branching skirt, morphs and a BMP texture. The tests check:

- the PMX round trip,
- the glTF structure,
- the humanoid map,
- the T-pose,
- the expressions, springs and colliders,
- that every node rotation is the identity.

If `tools/validator` has its npm package installed (`npm install` there), the tests also run the Khronos glTF-Validator. Set `PMX2VRM_NODE` to a `node` executable if `node` is not on the PATH.

Real MMD models for manual testing go in `samples/`, which is git-ignored. Never commit third-party models.
