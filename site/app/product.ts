import { productVersion } from "./version.generated";

export const product = {
  name: "PackageMedic",
  packageId: "PackageMedic.Tool",
  version: productVersion,
  command: "package-medic",
} as const;

export const isPrerelease = product.version.includes("-");
export const releaseLabel = isPrerelease ? "Development preview" : "Stable release";
export const releaseNoun = isPrerelease ? "preview" : "stable release";

export const installCommand =
  `dotnet tool install --global ${product.packageId} --version ${product.version}`;

export const doctorCommand = `${product.command} doctor`;
export const reportCommand =
  `${product.command} doctor . --format json --output reports/medic.json --sarif-output reports/medic.sarif`;
export const initCommand = `${product.command} init`;
export const baselineCommand =
  `${product.command} baseline create . --output .packagemedic-baseline.json`;
export const newOnlyCommand =
  `${product.command} doctor . --fail-on none --fail-on-new warning`;
export const nugetUrl = `https://www.nuget.org/packages/${product.packageId}`;
export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "";
export const assetPath = (path: string) =>
  `${basePath}/${path.replace(/^\/+/, "")}`;
