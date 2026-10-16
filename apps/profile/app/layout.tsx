import './globals.css';
import { ThemeSync } from './theme-sync';

export const metadata = { title: 'Excellore Profile' };

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        <ThemeSync />
        {children}
      </body>
    </html>
  );
}
