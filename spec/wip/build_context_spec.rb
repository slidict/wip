# frozen_string_literal: true

require 'spec_helper'
require 'fileutils'
RSpec.describe Wip::BuildContext do
  it 'yields the original context untouched when there is no .dockerignore' do
    Dir.mktmpdir do |dir|
      FileUtils.touch(File.join(dir, 'app.rb'))

      described_class.new(dir).stage { |staged| expect(staged).to eq(File.expand_path(dir)) }
    end
  end

  it 'stages a copy of the context with ignored files and directories left out' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, '.dockerignore'), "node_modules\n*.log\n")
      File.write(File.join(dir, 'app.rb'), 'puts 1')
      File.write(File.join(dir, 'debug.log'), 'noisy')
      FileUtils.mkdir_p(File.join(dir, 'node_modules', 'pkg'))
      File.write(File.join(dir, 'node_modules', 'pkg', 'index.js'), '')

      staged_files = nil
      described_class.new(dir).stage do |staged|
        staged_files = Dir.glob('**/*', File::FNM_DOTMATCH, base: staged).reject { |f| %w[. ..].include?(f) }
        expect(File.read(File.join(staged, 'app.rb'))).to eq('puts 1')
      end

      expect(staged_files).to include('app.rb', '.dockerignore')
      expect(staged_files).not_to include('debug.log')
      expect(staged_files.grep(/^node_modules/)).to be_empty
    end
  end

  it 'preserves symlinks instead of copying files from outside the context' do
    Dir.mktmpdir do |parent|
      context = File.join(parent, 'context')
      FileUtils.mkdir_p(context)
      File.write(File.join(context, '.dockerignore'), "ignored\n")
      secret = File.join(parent, 'secret')
      File.write(secret, 'host secret')
      File.symlink(secret, File.join(context, 'linked-secret'))

      described_class.new(context).stage do |staged|
        staged_link = File.join(staged, 'linked-secret')
        expect(File).to be_symlink(staged_link)
        expect(File.readlink(staged_link)).to eq(secret)
      end
    end
  end

  it 'calls on_progress before copying and after every staged file, and not at all when staging is skipped' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, '.dockerignore'), "node_modules\n")
      File.write(File.join(dir, 'app.rb'), '')
      File.write(File.join(dir, 'app_spec.rb'), '')
      FileUtils.mkdir_p(File.join(dir, 'node_modules'))
      File.write(File.join(dir, 'node_modules', 'pkg.js'), '')

      calls = []
      described_class.new(dir).stage(on_progress: ->(count, total) { calls << [count, total] }) { |_staged| nil }

      # .dockerignore, app.rb, app_spec.rb — node_modules/pkg.js is excluded.
      expect(calls).to eq([[0, 3], [1, 3], [2, 3], [3, 3]])
    end

    Dir.mktmpdir do |dir|
      FileUtils.touch(File.join(dir, 'app.rb'))

      calls = []
      described_class.new(dir).stage(on_progress: ->(count, total) { calls << [count, total] }) { |_staged| nil }

      expect(calls).to eq([])
    end
  end

  it 'only calls on_progress once the file it reports has actually finished copying' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'app.rb'), '')
      File.write(File.join(dir, 'app_spec.rb'), '')
      # A rule that matches nothing forces staging (the empty-.dockerignore
      # case skips copying entirely and never calls on_progress at all).
      File.write(File.join(dir, '.dockerignore'), "nonexistent-dir/\n")

      completed = 0
      allow(FileUtils).to receive(:copy_entry).and_wrap_original do |original, *args|
        result = original.call(*args)
        completed += 1
        result
      end

      counts_seen_by_progress = []
      described_class.new(dir).stage(on_progress: lambda { |count, _total|
        counts_seen_by_progress << [count, completed]
      }) { |_staged| nil }

      expect(counts_seen_by_progress).to eq([[0, 0], [1, 1], [2, 2], [3, 3]])
    end
  end

  it 'never descends into an ignored directory, even one full of files' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, '.dockerignore'), "node_modules\n")
      File.write(File.join(dir, 'app.rb'), '')
      FileUtils.mkdir_p(File.join(dir, 'node_modules', 'pkg'))
      File.write(File.join(dir, 'node_modules', 'pkg', 'index.js'), '')

      visited = []
      allow(File).to receive(:directory?).and_wrap_original do |original, path, *args|
        visited << path
        original.call(path, *args)
      end

      staged_files = nil
      described_class.new(dir).stage do |staged|
        staged_files = Dir.glob('**/*', File::FNM_DOTMATCH, base: staged).reject { |f| %w[. ..].include?(f) }
      end

      expect(staged_files).to include('app.rb', '.dockerignore')
      expect(staged_files.grep(/^node_modules/)).to be_empty
      expect(visited.grep(%r{node_modules/pkg})).to be_empty
    end
  end

  it 'stages a file re-included by a negated rule under an otherwise-ignored directory' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, '.dockerignore'), "node_modules\n!node_modules/pkg/index.js\n")
      FileUtils.mkdir_p(File.join(dir, 'node_modules', 'pkg'))
      File.write(File.join(dir, 'node_modules', 'pkg', 'index.js'), '')
      File.write(File.join(dir, 'node_modules', 'pkg', 'other.js'), '')

      staged_files = nil
      described_class.new(dir).stage do |staged|
        staged_files = Dir.glob('**/*', File::FNM_DOTMATCH, base: staged).reject { |f| %w[. ..].include?(f) }
      end

      expect(staged_files).to include(File.join('node_modules', 'pkg', 'index.js'))
      expect(staged_files).not_to include(File.join('node_modules', 'pkg', 'other.js'))
    end
  end

  it 'excludes special files like named pipes even when not ignored' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, 'app.rb'), '')
      # A rule that matches nothing forces staging (an empty .dockerignore
      # skips staging entirely and would just yield the original directory).
      File.write(File.join(dir, '.dockerignore'), "nonexistent-dir/\n")
      fifo = File.join(dir, 'a-fifo')
      system('mkfifo', fifo, exception: true)

      staged_files = nil
      described_class.new(dir).stage do |staged|
        staged_files = Dir.glob('**/*', File::FNM_DOTMATCH, base: staged).reject { |f| %w[. ..].include?(f) }
      end

      expect(staged_files).to include('app.rb', '.dockerignore')
      expect(staged_files).not_to include('a-fifo')
    end
  end

  it 'preserves broken symlinks' do
    Dir.mktmpdir do |dir|
      File.write(File.join(dir, '.dockerignore'), "ignored\n")
      File.symlink('missing', File.join(dir, 'broken'))

      described_class.new(dir).stage do |staged|
        expect(File).to be_symlink(File.join(staged, 'broken'))
      end
    end
  end
end
