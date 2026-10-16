import './globals.css';

export const metadata = {
  title: 'Excellore — One place for every workspace',
  description:
    'Excellore brings your organizations, apps, identity, and access together in one secure workspace.',
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
