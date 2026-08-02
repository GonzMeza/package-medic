# PackageMedic website

The animated landing page for PackageMedic. It introduces the experimental
NuGet dependency doctor, its diagnostics, read-only safety model, and install
flow.

## Prerequisites

- Node.js `>=22.13.0`

## Quick start

```bash
npm install
npm run dev
```

## Commands

- `npm run dev`: start the local preview
- `npm run build`: create and verify the production build
- `npm run lint`: run the code-quality checks
- `npm test`: build and smoke-test the rendered landing page

## Updating the release

Change `product.version` in `app/product.ts`. The preview label, install command,
and terminal example all reuse that single value.

## Links

- [PackageMedic repository](https://github.com/GonzMeza/package-medic)
- [PackageMedic on NuGet](https://www.nuget.org/packages/PackageMedic.Tool)
- [vinext documentation](https://github.com/cloudflare/vinext)
