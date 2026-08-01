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
end
