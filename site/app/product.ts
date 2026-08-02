import { productVersion } from "./version.generated";

export const product = {
  name: "PackageMedic",
  packageId: "PackageMedic.Tool",
  version: productVersion,
  command: "package-medic",
} as const;

export const installCommand =
  `dotnet tool install --global ${product.packageId} --version ${product.version}`;

export const doctorCommand = `${product.command} doctor`;
export const nugetUrl = `https://www.nuget.org/packages/${product.packageId}`;
export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "";
export const assetPath = (path: string) =>
  `${basePath}/${path.replace(/^\/+/, "")}`;
