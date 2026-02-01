#include "projecttree.h"
#include <QDir>
#include <QFileInfo>
#include <QHeaderView>
#include <QStyle>

ProjectTree::ProjectTree(QWidget *parent)
    : QTreeWidget(parent)
    , m_watcher(new QFileSystemWatcher(this))
{
    setHeaderHidden(true);
    setAnimated(true);
    setIndentation(16);
    setUniformRowHeights(true);
    
    connect(this, &QTreeWidget::itemDoubleClicked, this, &ProjectTree::onItemDoubleClicked);
    connect(m_watcher, &QFileSystemWatcher::directoryChanged, this, &ProjectTree::onDirectoryChanged);
}

void ProjectTree::loadProject(const QString &path)
{
    clearProject();
    
    if (path.isEmpty() || !QDir(path).exists()) {
        return;
    }
    
    m_projectPath = path;
    
    // Create root item
    QDir dir(path);
    QTreeWidgetItem *root = createItem(dir.dirName(), path, true);
    root->setExpanded(true);
    addTopLevelItem(root);
    
    // Populate tree
    populateTree(path, root);
    
    // Watch the project directory
    m_watcher->addPath(path);
}

void ProjectTree::clearProject()
{
    clear();
    m_projectPath.clear();
    
    QStringList paths = m_watcher->directories();
    if (!paths.isEmpty()) {
        m_watcher->removePaths(paths);
    }
}

void ProjectTree::populateTree(const QString &path, QTreeWidgetItem *parent)
{
    QDir dir(path);
    
    // Skip these directories
    static QStringList skipDirs = {"bin", "obj", ".git", ".vs", "node_modules", "Intermediate", "Binaries"};
    
    // Directories first
    QFileInfoList dirs = dir.entryInfoList(QDir::Dirs | QDir::NoDotAndDotDot, QDir::Name);
    for (const QFileInfo &info : dirs) {
        if (skipDirs.contains(info.fileName()) || info.fileName().startsWith('.')) {
            continue;
        }
        
        QTreeWidgetItem *item = createItem(info.fileName(), info.absoluteFilePath(), true);
        parent->addChild(item);
        
        populateTree(info.absoluteFilePath(), item);
        m_watcher->addPath(info.absoluteFilePath());
    }
    
    // Files - prioritize build files
    QStringList filters = {"*.target.cs", "*.module.cs", "*.build.cs", "*.cs", "*.cpp", "*.c", "*.h", "*.hpp", "*.json", "*.xml"};
    QFileInfoList files = dir.entryInfoList(filters, QDir::Files, QDir::Name);
    
    for (const QFileInfo &info : files) {
        if (info.fileName().startsWith('.')) {
            continue;
        }
        
        QTreeWidgetItem *item = createItem(info.fileName(), info.absoluteFilePath(), false);
        parent->addChild(item);
    }
}

QTreeWidgetItem* ProjectTree::createItem(const QString &name, const QString &path, bool isDir)
{
    QTreeWidgetItem *item = new QTreeWidgetItem();
    item->setText(0, name);
    item->setData(0, Qt::UserRole, path);
    
    if (isDir) {
        item->setIcon(0, style()->standardIcon(QStyle::SP_DirIcon));
    } else {
        item->setIcon(0, style()->standardIcon(QStyle::SP_FileIcon));
    }
    
    return item;
}

void ProjectTree::onItemDoubleClicked(QTreeWidgetItem *item, int column)
{
    Q_UNUSED(column)
    
    QString path = item->data(0, Qt::UserRole).toString();
    QFileInfo info(path);
    
    if (info.isFile()) {
        emit fileActivated(path);
    }
}

void ProjectTree::onDirectoryChanged(const QString &path)
{
    Q_UNUSED(path)
    
    // Reload the project tree
    if (!m_projectPath.isEmpty()) {
        QString savedPath = m_projectPath;
        loadProject(savedPath);
    }
}
