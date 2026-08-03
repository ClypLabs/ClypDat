import Header from "./components/Header";
import Hero from "./components/Hero";
import Features from "./components/Features";
import GamesStrip from "./components/GamesStrip";
import Editor from "./components/Editor";
import Download from "./components/Download";
import Footer from "./components/Footer";

export default function Home() {
  return (
    <div className="flex flex-1 flex-col">
      <Header />
      <main className="flex-1">
        <Hero />
        <Features />
        <GamesStrip />
        <Editor />
        <Download />
      </main>
      <Footer />
    </div>
  );
}
