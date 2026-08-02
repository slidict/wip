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
