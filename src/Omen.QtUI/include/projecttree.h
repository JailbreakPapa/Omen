#ifndef PROJECTTREE_H
#define PROJECTTREE_H

#include <QTreeWidget>
#include <QFileSystemWatcher>

class ProjectTree : public QTreeWidget
{
    Q_OBJECT

public:
    explicit ProjectTree(QWidget *parent = nullptr);

    void loadProject(const QString &path);
    void clearProject();
    QString projectPath() const { return m_projectPath; }

signals:
    void fileActivated(const QString &path);

private slots:
    void onItemDoubleClicked(QTreeWidgetItem *item, int column);
    void onDirectoryChanged(const QString &path);

private:
    void populateTree(const QString &path, QTreeWidgetItem *parent);
    QTreeWidgetItem* createItem(const QString &name, const QString &path, bool isDir);

    QString m_projectPath;
    QFileSystemWatcher *m_watcher;
};

#endif // PROJECTTREE_H
