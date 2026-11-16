import { IconBox, IconBrandDebian, IconBrandUbuntu, IconBrandWindows, IconDatabase, IconServer } from "@tabler/icons-react";
import type { OsFamily } from "../api";

/** Kleines Symbol je Betriebssystem, damit man Hosts in langen Listen schneller erkennt. */
export function OsIcon({ osName, osFamily, size = 18 }: { osName: string | null; osFamily: OsFamily; size?: number }) {
  const name = (osName ?? "").toLowerCase();
  if (osFamily === "Windows") return <IconBrandWindows size={size} color="var(--mantine-color-blue-4)" />;
  if (name.includes("proxmox backup")) return <IconDatabase size={size} color="var(--mantine-color-orange-4)" />;
  if (name.includes("proxmox")) return <IconBox size={size} color="var(--mantine-color-orange-4)" />;
  if (name.includes("ubuntu")) return <IconBrandUbuntu size={size} color="var(--mantine-color-orange-5)" />;
  if (name.includes("debian")) return <IconBrandDebian size={size} color="var(--mantine-color-pink-5)" />;
  return <IconServer size={size} />;
}
